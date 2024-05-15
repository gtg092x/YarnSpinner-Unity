/*
Yarn Spinner is licensed to you under the terms found in the file LICENSE.md.
*/

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Text;
using System;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using Yarn.Unity.ActionAnalyser;
using YarnAction = Yarn.Unity.ActionAnalyser.Action;
using System.IO;

#nullable enable

static class ErrorCodes {
    public const string YS1001ActionMethodsMustBePublic = "YS1001";
    public const string YS1002ActionMethodsMustHaveAValidName = "YS1002";

    public const string YS1003ActionMethodsMustHaveAValidReturnType = "YS1003";
    public const string YS1003ActionMethodsMustHaveValidParameters = "YS1004";
}

[Generator]
public class ActionRegistrationSourceGenerator : ISourceGenerator
{
    const string YarnSpinnerUnityAssemblyName = "YarnSpinner.Unity";
    const string DebugLoggingPreprocessorSymbol = "YARN_SOURCE_GENERATION_DEBUG_LOGGING";
    const string MinimumUnityVersionPreprocessorSymbol = "UNITY_2021_2_OR_NEWER";

    public static string? GetProjectRoot(GeneratorExecutionContext context)
    {
        // Try and find any additional files passed to the context
        if (!context.AdditionalFiles.Any())
        {
            return null;
        }

        // One of those files is (AssemblyName).AdditionalFile.txt, and it
        // contains the path to the project
        var relevantFiles = context.AdditionalFiles.Where(i => i.Path.Contains($"{context.Compilation.AssemblyName}.AdditionalFile.txt"));
        if (!relevantFiles.Any())
        {
            return null;
        }

        var assemblyRelevantFile = relevantFiles.First();

        // The file needs to exist on disk
        if (!File.Exists(assemblyRelevantFile.Path))
        {
            return null;
        }

        try
        {
            // Attempt to read it - it should contain the path to the project directory
            var projectPath = File.ReadAllText(assemblyRelevantFile.Path);
            if (Directory.Exists(projectPath))
            {
                // If this directory exists, we're done
                return projectPath;
            } else {
                return null;
            }
        }
        catch (IOException)
        {
            // We encountered a problem while testing
            return null;
        }
    }

    public void Execute(GeneratorExecutionContext context)
    {
        using var output = GetOutput(context);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();


        output.WriteLine(DateTime.Now);

        // We need to know if the settings are configured to not perform codegen
        // to link attributed methods. This is kinda annoying because the path
        // root of the project settings and the root path of this process are
        // *very* different. So, what we do is we use the included Compilation
        // Assembly additional file that Unity gives us. This file, if opened,
        // has the path of the Unity project, which we can then use to get the
        // settings. If any stage of this fails, then we bail out and assume
        // that codegen is desired.
        string? projectPath = null;
        Yarn.Unity.Editor.YarnSpinnerProjectSettings? settings = null;
        if (context.AdditionalFiles.Any())
        {
            var relevants = context.AdditionalFiles.Where(i => i.Path.Contains($"{context.Compilation.AssemblyName}.AdditionalFile.txt"));
            if (relevants.Any())
            {
                var arsgacsaf = relevants.First();
                if (File.Exists(arsgacsaf.Path))
                {
                    try
                    {
                        projectPath = File.ReadAllText(arsgacsaf.Path);
                        var fullPath = Path.Combine(projectPath, Yarn.Unity.Editor.YarnSpinnerProjectSettings.YarnSpinnerProjectSettingsPath);
                        output.WriteLine($"Attempting to read settings file at {fullPath}");

                        settings = Yarn.Unity.Editor.YarnSpinnerProjectSettings.GetOrCreateSettings(projectPath, output);
                        if (!settings.automaticallyLinkAttributedYarnCommandsAndFunctions)
                        {
                            output.WriteLine("Skipping codegen due to settings.");
                            return;
                        }
                    }
                    catch (Exception e)
                    {
                        output.WriteLine($"Unable to determine Yarn settings, settings values will be ignored and codegen will occur: {e.Message}");
                    }
                }
                else
                {
                    output.WriteLine($"The project settings path metadata file does not exist at: {arsgacsaf.Path}. Settings values will be ignored and codegen will occur");
                }
            }
            else
            {
                output.WriteLine("Unable to determine Yarn settings path, no file containing the project path metadata was included. Settings values will be ignored and codegen will occur.");
            }
        }
        else
        {
            output.WriteLine("Unable to determine Yarn settings path as no additional files were included. Settings values will be ignored and codegen will occur.");
        }

        try
        {
            output.WriteLine("Source code generation for assembly " + context.Compilation.AssemblyName);

            if (context.AdditionalFiles.Any())
            {
                output.WriteLine($"Additional files:");
                foreach (var item in context.AdditionalFiles)
                {
                    output.WriteLine("  " + item.Path);
                }
            }

            output.WriteLine("Referenced assemblies for this compilation:");
            foreach (var referencedAssembly in context.Compilation.ReferencedAssemblyNames)
            {
                output.WriteLine(" - " + referencedAssembly.Name);
            }

            bool compilationReferencesYarnSpinner = context.Compilation.ReferencedAssemblyNames
                .Any(name => name.Name == YarnSpinnerUnityAssemblyName);

            if (compilationReferencesYarnSpinner == false)
            {
                // This compilation doesn't reference YarnSpinner.Unity. Any
                // code that we generate that references symbols in that
                // assembly won't work.
                output.WriteLine($"Assembly {context.Compilation.AssemblyName} doesn't reference {YarnSpinnerUnityAssemblyName}. Not generating any code for it.");
                return;
            }

            output.WriteLine("Preprocessor Symbols: ");
            foreach (var symbol in context.ParseOptions.PreprocessorSymbolNames)
            {
                output.WriteLine("- " + symbol);
            }

            // Don't generate source code if we're not targeting at least Unity
            // 2021.2. (Unity will not invoke this DLL as a source code
            // generator until at least this version, but other tools like
            // OmniSharp might.)
            if (!context.ParseOptions.PreprocessorSymbolNames.Contains(MinimumUnityVersionPreprocessorSymbol))
            {
                output.WriteLine($"Not generating code for assembly {context.Compilation.AssemblyName} because this assembly is not being built for Unity 2021.2 or newer");
                return;
            }


            // Don't generate source code for certain Yarn Spinner provided
            // assemblies - these always manually register any actions in them.
            var prefixesToIgnore = new List<string>()
            {
                "YarnSpinner.Unity",
                "YarnSpinner.Editor",
            };

            if (context.Compilation.AssemblyName == null)
            {
                output.WriteLine("Not generating registration code, because the provided AssemblyName is null");
                return;
            }
            foreach (var prefix in prefixesToIgnore)
            {
                if (context.Compilation.AssemblyName.StartsWith(prefix))
                {
                    output.WriteLine($"Not generating registration code for {context.Compilation.AssemblyName}: we've been told to exclude it, because its name begins with one of these prefixes: {string.Join(", ", prefixesToIgnore)}");
                    return;
                }
            }

            if (!(context.Compilation is CSharpCompilation compilation))
            {
                // This is not a C# compilation, so we can't do analysis.
                output.WriteLine($"Stopping code generation because compilation is not a {nameof(CSharpCompilation)}.");
                return;
            }

            var actions = new List<YarnAction>();
            foreach (var tree in compilation.SyntaxTrees)
            {
                actions.AddRange(Analyser.GetActions(compilation, tree, output).Where(a => a.DeclarationType == DeclarationType.Attribute));
            }

            if (actions.Any() == false)
            {
                output.WriteLine($"Didn't find any Yarn Actions in {context.Compilation.AssemblyName}. Not generating any source code for it.");
                return;
            }

            List<ITypeSymbol> knownValidReturnTypes = new List<ITypeSymbol?> {
                    compilation.GetTypeByMetadataName("UnityEngine.Coroutine"),
                    compilation.GetTypeByMetadataName("System.Collections.IEnumerator"),
                    compilation.GetSpecialType(SpecialType.System_Void),
                }
                .NonNull(throwIfAnyNull: true)
                .ToList();


            HashSet<string> removals = new HashSet<string>();
            // validating and logging all the actions
            foreach (var action in actions)
            {
                if (action == null)
                {
                    output.WriteLine($"Action is null??");
                    continue;
                }

                output.WriteLine($"{action.Name} ({action.MethodName}) has return type {action.MethodSymbol.ReturnType}");


                bool isReturnTypeOnAllowList = knownValidReturnTypes.Contains(action.MethodSymbol.ReturnType);
                output.WriteLine($"{action.Name} ({action.MethodName}) has is allowlisted: {isReturnTypeOnAllowList}");

                // TODO: Allow async actions (i.e. action methods that return an
                // awaitable type) once support for them is added to the actions
                // system

                // bool isReturnTypeAwaitable = action.MethodSymbol.ReturnType.IsAwaitableNonDynamic(action.SemanticModel, action.MethodDeclarationSyntax.SpanStart);
                // output.WriteLine($"{action.Name} ({action.MethodName}) has is awaitable: {isReturnTypeAwaitable}");

                bool isReturnTypeValid = isReturnTypeOnAllowList;

                if (!isReturnTypeValid)
                {
                    var descriptor = new DiagnosticDescriptor(
                        ErrorCodes.YS1003ActionMethodsMustHaveAValidReturnType,
                        $"Yarn {action.Type} methods must return a valid type",
                        $"YarnCommand and YarnFunction methods must return a valid type ({string.Join(", ", knownValidReturnTypes.Select(t => t.ToDisplayString()))}). \"{{0}}\" is {{1}}.",
                        "Yarn Spinner",
                        DiagnosticSeverity.Warning,
                        true,
                        "[YarnCommand] and [YarnFunction] attributed methods must be public so that the codegen can reference them.",
                        "https://docs.yarnspinner.dev/using-yarnspinner-with-unity/creating-commands-functions");
                    context.ReportDiagnostic(Microsoft.CodeAnalysis.Diagnostic.Create(
                        descriptor,
                        action.Declaration?.GetLocation(),
                        action.MethodIdentifierName, action.MethodSymbol.ReturnType.ToDisplayString()
                    ));
                    output.WriteLine($"Action {action.Name} will be skipped due to its return type not being valid ({action.MethodSymbol.ReturnType})");
                    removals.Add(action.Name);
                    continue;
                }

                // if an action isn't public we will log a warning
                // and then later we will also skip it
                if (action.MethodSymbol.DeclaredAccessibility != Accessibility.Public)
                {
                    var descriptor = new DiagnosticDescriptor(
                        ErrorCodes.YS1001ActionMethodsMustBePublic,
                        $"Yarn {action.Type} methods must be public",
                        "YarnCommand and YarnFunction methods must be public. \"{0}\" is {1}.",
                        "Yarn Spinner",
                        DiagnosticSeverity.Warning,
                        true,
                        "[YarnCommand] and [YarnFunction] attributed methods must be public so that the codegen can reference them.",
                        "https://docs.yarnspinner.dev/using-yarnspinner-with-unity/creating-commands-functions");
                    context.ReportDiagnostic(Microsoft.CodeAnalysis.Diagnostic.Create(
                        descriptor,
                        action.Declaration?.GetLocation(),
                        action.MethodIdentifierName, action.MethodSymbol.DeclaredAccessibility
                    ));
                    output.WriteLine($"Action {action.Name} will be skipped due to it's declared accessibility {action.MethodSymbol.DeclaredAccessibility}");
                    removals.Add(action.Name);
                    continue;
                }

                // this is not a full validation of the naming rules of commands
                // but is good enough to catch the most common situations, whitespace and periods
                if (action.Name.Contains(".") || action.Name.Any(x => Char.IsWhiteSpace(x)))
                {
                    var descriptor = new DiagnosticDescriptor(
                        ErrorCodes.YS1002ActionMethodsMustHaveAValidName,
                        $"Yarn {action.Type} methods must have a valid name",
                        "YarnCommand and YarnFunction methods follow existing ID rules for Yarn. \"{0}\" is invalid.",
                        "Yarn Spinner",
                        DiagnosticSeverity.Warning,
                        true,
                        "[YarnCommand] and [YarnFunction] attributed methods must follow Yarn ID rules so that Yarn scripts can reference them.",
                        "https://docs.yarnspinner.dev/using-yarnspinner-with-unity/creating-commands-functions");
                    context.ReportDiagnostic(Microsoft.CodeAnalysis.Diagnostic.Create(
                        descriptor,
                        action.Declaration?.GetLocation(),
                        action.Name
                    ));
                    output.WriteLine($"Action {action.MethodIdentifierName} will be skipped due to it's name {action.Name}");
                    removals.Add(action.Name);
                    continue;
                }

                output.WriteLine($"Action {action.Name}: {action.SourceFileName}:{action.Declaration?.GetLocation()?.GetLineSpan().StartLinePosition.Line} ({action.Type})");
            }

            // removing any actions that failed validation
            actions = actions.Where(x => !removals.Contains(x.Name)).ToList();

            output.Write($"Generating source code...");

            var source = Analyser.GenerateRegistrationFileSource(actions);

            output.WriteLine($"Done.");

            SourceText sourceText = SourceText.From(source, Encoding.UTF8);

            output.Write($"Writing generated source...");

            DumpGeneratedFile(context, source);

            output.WriteLine($"Done.");

            context.AddSource($"YarnActionRegistration-{compilation.AssemblyName}.Generated.cs", sourceText);

            if (settings != null)
            {
                if (settings.generateYSLSFile)
                {
                    output.Write($"Generating ysls...");
                    // generating the ysls
                    YSLSGenerator generator = new YSLSGenerator(output);

                    foreach (var action in actions)
                    {
                        generator.AddAction(action);
                    }
                    var ysls = generator.Serialise();
                    output.WriteLine($"Done.");

                    if (!string.IsNullOrEmpty(projectPath))
                    {
                        output.Write($"Writing generated ysls...");

                        var fullPath = Path.Combine(projectPath, Yarn.Unity.Editor.YarnSpinnerProjectSettings.YarnSpinnerGeneratedYSLSPath);
                        try
                        {
                            System.IO.File.WriteAllText(fullPath, ysls);
                            output.WriteLine($"Done.");
                        }
                        catch (Exception e)
                        {
                            output.WriteLine($"Unable to write ysls to disk: {e.Message}");
                        }
                    }
                    else
                    {
                        output.WriteLine("unable to identify project path, ysls will not be written to disk");
                    }
                }
                else
                {
                    output.WriteLine($"skipping ysls generation due to settings");
                }
            }
            else
            {
                output.WriteLine($"skipping ysls generation due to settings not being found");
            }

            stopwatch.Stop();
            output.WriteLine($"Source code generation completed in {stopwatch.Elapsed.TotalMilliseconds}ms");
            return;

        }
        catch (Exception e)
        {
            output.WriteLine($"{e}");
        }
    }

    private MethodDeclarationSyntax GenerateLoggingMethod(string methodName, string sourceExpression, string prefix)
    {
        return SyntaxFactory.MethodDeclaration(
    SyntaxFactory.PredefinedType(
        SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
    SyntaxFactory.Identifier(methodName))
.WithModifiers(
    SyntaxFactory.TokenList(
        new[]{
            SyntaxFactory.Token(SyntaxKind.PublicKeyword),
            SyntaxFactory.Token(SyntaxKind.StaticKeyword)}))
.WithBody(
    SyntaxFactory.Block(
        SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.GenericName(
                    SyntaxFactory.Identifier("IEnumerable"))
                .WithTypeArgumentList(
                    SyntaxFactory.TypeArgumentList(
                        SyntaxFactory.SingletonSeparatedList<TypeSyntax>(
                            SyntaxFactory.PredefinedType(
                                SyntaxFactory.Token(SyntaxKind.StringKeyword))))))
            .WithVariables(
                SyntaxFactory.SingletonSeparatedList<VariableDeclaratorSyntax>(
                    SyntaxFactory.VariableDeclarator(
                        SyntaxFactory.Identifier("source"))
                    .WithInitializer(
                        SyntaxFactory.EqualsValueClause(
                            SyntaxFactory.ParseExpression(sourceExpression)))))),
        SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.IdentifierName(
                    SyntaxFactory.Identifier(
                        SyntaxFactory.TriviaList(),
                        SyntaxKind.VarKeyword,
                        "var",
                        "var",
                        SyntaxFactory.TriviaList())))
            .WithVariables(
                SyntaxFactory.SingletonSeparatedList<VariableDeclaratorSyntax>(
                    SyntaxFactory.VariableDeclarator(
                        SyntaxFactory.Identifier("prefix"))
                    .WithInitializer(
                        SyntaxFactory.EqualsValueClause(
                            SyntaxFactory.LiteralExpression(
                                SyntaxKind.StringLiteralExpression,
                                SyntaxFactory.Literal(prefix))))))),
        SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("Debug"),
                    SyntaxFactory.IdentifierName("Log")
                )
            )
            .WithArgumentList(
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList<ArgumentSyntax>(
                        SyntaxFactory.Argument(
                            SyntaxFactory.InterpolatedStringExpression(
                                SyntaxFactory.Token(SyntaxKind.InterpolatedVerbatimStringStartToken)
                            )
                            .WithContents(
                                SyntaxFactory.List<InterpolatedStringContentSyntax>(
                                    new InterpolatedStringContentSyntax[]{
                                        SyntaxFactory.Interpolation(
                                            SyntaxFactory.IdentifierName("prefix")
                                        ),
                                        SyntaxFactory.InterpolatedStringText()
                                        .WithTextToken(
                                            SyntaxFactory.Token(
                                                SyntaxFactory.TriviaList(),
                                                SyntaxKind.InterpolatedStringTextToken,
                                                " ",
                                                " ",
                                                SyntaxFactory.TriviaList()
                                            )
                                        ),
                                        SyntaxFactory.Interpolation(
                                            SyntaxFactory.InvocationExpression(
                                                SyntaxFactory.MemberAccessExpression(
                                                    SyntaxKind.SimpleMemberAccessExpression,
                                                    SyntaxFactory.PredefinedType(
                                                        SyntaxFactory.Token(SyntaxKind.StringKeyword)
                                                    ),
                                                    SyntaxFactory.IdentifierName("Join")
                                                )
                                            )
                                            .WithArgumentList(
                                                SyntaxFactory.ArgumentList(
                                                    SyntaxFactory.SeparatedList<ArgumentSyntax>(
                                                        new SyntaxNodeOrToken[]{
                                                            SyntaxFactory.Argument(
                                                                SyntaxFactory.LiteralExpression(
                                                                    SyntaxKind.CharacterLiteralExpression,
                                                                    SyntaxFactory.Literal(';')
                                                                )
                                                            ),
                                                            SyntaxFactory.Token(SyntaxKind.CommaToken),
                                                            SyntaxFactory.Argument(
                                                                SyntaxFactory.IdentifierName("source")
                                                            )
                                                        }
                                                    )
                                                )
                                            )
                                        )
                                    }
                                )
                            )
                        )
                    )
                )
            )
        )
    )
)
.NormalizeWhitespace();
    }

    public static MethodDeclarationSyntax GenerateSingleLogMethod(string methodName, string text, string prefix)
    {
        return SyntaxFactory.MethodDeclaration(
            SyntaxFactory.PredefinedType(
                SyntaxFactory.Token(SyntaxKind.VoidKeyword)
            ),
            SyntaxFactory.Identifier(methodName)
        )
        .WithModifiers(
            SyntaxFactory.TokenList(
                new[]{
                    SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                    SyntaxFactory.Token(SyntaxKind.StaticKeyword)
                }
            )
        )
        .WithBody(
            SyntaxFactory.Block(
                SyntaxFactory.SingletonList<StatementSyntax>(
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName("Debug"),
                                SyntaxFactory.IdentifierName("Log")
                            )
                        )
                        .WithArgumentList(
                            SyntaxFactory.ArgumentList(
                                SyntaxFactory.SingletonSeparatedList<ArgumentSyntax>(
                                    SyntaxFactory.Argument(
                                        SyntaxFactory.InterpolatedStringExpression(
                                            SyntaxFactory.Token(SyntaxKind.InterpolatedStringStartToken)
                                        )
                                        .WithContents(
                                            SyntaxFactory.List<InterpolatedStringContentSyntax>(
                                                new InterpolatedStringContentSyntax[]{
                                                    SyntaxFactory.Interpolation(
                                                        SyntaxFactory.LiteralExpression(
                                                            SyntaxKind.StringLiteralExpression,
                                                            SyntaxFactory.Literal(prefix)
                                                        )
                                                    ),
                                                    SyntaxFactory.InterpolatedStringText()
                                                    .WithTextToken(
                                                        SyntaxFactory.Token(
                                                            SyntaxFactory.TriviaList(),
                                                            SyntaxKind.InterpolatedStringTextToken,
                                                            " ",
                                                            " ",
                                                            SyntaxFactory.TriviaList()
                                                        )
                                                    ),
                                                    SyntaxFactory.Interpolation(
                                                        SyntaxFactory.LiteralExpression(
                                                            SyntaxKind.StringLiteralExpression,
                                                            SyntaxFactory.Literal(text)
                                                        )
                                                    )
                                                }
                                            )
                                        )
                                    )
                                )
                            )
                        )
                    )
                )
            )
        )
        .NormalizeWhitespace();
    }

    public void Initialize(GeneratorInitializationContext context)
    {
        context.RegisterForSyntaxNotifications(() => new ClassDeclarationSyntaxReceiver());
    }

    static string GetTemporaryPath(GeneratorExecutionContext context)
    {
        string tempPath;
        var rootPath = GetProjectRoot(context);
        if (rootPath != null) {
            tempPath = Path.Combine(rootPath, "Logs", "Packages", "dev.yarnspinner.unity");
        } else {
            tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dev.yarnspinner.logs");
        }

        // we need to make the logs folder, but this can potentially fail
        // if it does fail then we will just chuck the logs inside the tmp folder
        try
        {
            if (!Directory.Exists(tempPath))
            {
                Directory.CreateDirectory(tempPath);
            }
        }
        catch
        {
            tempPath = System.IO.Path.GetTempPath();
        }
        return tempPath;
    }

    public Yarn.Unity.ILogger GetOutput(GeneratorExecutionContext context)
    {
        if (GetShouldLogToFile(context))
        {
            var tempPath = ActionRegistrationSourceGenerator.GetTemporaryPath(context);

            var path = System.IO.Path.Combine(tempPath, $"{nameof(ActionRegistrationSourceGenerator)}-{context.Compilation.AssemblyName}.txt");
            var outFile = System.IO.File.Open(path, System.IO.FileMode.Create);

            return new Yarn.Unity.FileLogger(new System.IO.StreamWriter(outFile));
        }
        else
        {
            return new Yarn.Unity.NullLogger();
        }
    }

    private static bool GetShouldLogToFile(GeneratorExecutionContext context)
    {
        return context.ParseOptions.PreprocessorSymbolNames.Contains(DebugLoggingPreprocessorSymbol);
    }

    public void DumpGeneratedFile(GeneratorExecutionContext context, string text)
    {
        if (GetShouldLogToFile(context))
        {
            var tempPath = ActionRegistrationSourceGenerator.GetTemporaryPath(context);
            var path = System.IO.Path.Combine(tempPath, $"{nameof(ActionRegistrationSourceGenerator)}-{context.Compilation.AssemblyName}.cs");
            System.IO.File.WriteAllText(path, text);
        }
    }
}

internal class ClassDeclarationSyntaxReceiver : ISyntaxReceiver
{
    public List<ClassDeclarationSyntax> Classes { get; private set; } = new List<ClassDeclarationSyntax>();

    public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
    {
        // Business logic to decide what we're interested in goes here
        if (syntaxNode is ClassDeclarationSyntax cds)
        {
            Classes.Add(cds);
        }
    }
}

internal class YSLSGenerator
{
    public YSLSGenerator(Yarn.Unity.ILogger logger) {
        this.logger = logger;
    }
    struct YarnActionParameter
    {
        internal string Name;
        internal string Type;
        internal bool IsParamsArray;

        internal Dictionary<string, object> ToDictionary()
        {
            var dict = new Dictionary<string, object>();
            dict["Name"] = Name;
            dict["Type"] = Type;
            dict["IsParamsArray"] = IsParamsArray;
            return dict;
        }
    }
    struct YarnActionCommand
    {
        internal string YarnName;
        internal string DefinitionName;
        internal string Signature;
        internal string FileName;
        internal YarnActionParameter[] Parameters;

        internal Dictionary<string, object> ToDictionary()
        {
            var dict = new Dictionary<string, object>();
            dict["YarnName"] = YarnName;
            dict["DefinitionName"] = DefinitionName;
            dict["Signature"] = Signature;
            dict["Language"] = "csharp";

            if (!string.IsNullOrEmpty(FileName))
            {
                dict["FileName"] = FileName;
            }

            if (Parameters.Length > 0)
            {
                var pl = new List<Dictionary<string, object>>();
                foreach (var p in Parameters)
                {
                    pl.Add(p.ToDictionary());
                }
                dict["Parameters"] = pl;
            }
            return dict;
        }
    }
    struct YarnActionFunction
    {
        internal string YarnName;
        internal string DefinitionName;
        internal string Signature;
        internal YarnActionParameter[] Parameters;
        internal string ReturnType;
        internal string FileName;

        internal Dictionary<string, object> ToDictionary()
        {
            var dict = new Dictionary<string, object>();
            dict["YarnName"] = YarnName;
            dict["DefinitionName"] = DefinitionName;
            dict["Signature"] = Signature;
            dict["ReturnType"] = ReturnType;
            dict["Language"] = "csharp";

            if (!string.IsNullOrEmpty(FileName))
            {
                dict["FileName"] = FileName;
            }

            if (Parameters.Length > 0)
            {
                var pl = new List<Dictionary<string, object>>();
                foreach (var p in Parameters)
                {
                    pl.Add(p.ToDictionary());
                }
                dict["Parameters"] = pl;
            }
            return dict;
        }
    }

    internal Yarn.Unity.ILogger logger;
    List<YarnActionCommand> commands = new List<YarnActionCommand>();
    List<YarnActionFunction> functions = new List<YarnActionFunction>();

    internal string Serialise()
    {
        var commandLine = "\"Commands\":[]";
        var functionLine = "\"Functions\":[]";
        // do we have any commands?
        if (commands.Count() > 0)
        {
            var result = string.Join(",", commands.Select(c => Yarn.Unity.Editor.Json.Serialize(c.ToDictionary())));
            commandLine = $"\"Commands\":[{result}]";
        }
        // do we have any functions?
        if (functions.Count() > 0)
        {
            var result = string.Join(",", functions.Select(f => Yarn.Unity.Editor.Json.Serialize(f.ToDictionary())));
            functionLine = $"\"Functions\":[{result}]";
        }
        return $"{{{commandLine},{functionLine}}}";
    }
    internal void AddAction(YarnAction action)
    {
        switch (action.Type)
        {
            case ActionType.Command:
                AddCommand(action);
                break;
            case ActionType.Function:
                AddFunction(action);
                break;
            case ActionType.NotAnAction:
                logger.WriteLine($"attempted to make a ysls string for {action.Name}, but it is not a command or function");
                break;
        }
    }
    private void AddFunction(YarnAction action)
    {
        var parameters = GenerateParams(action.Parameters);
        var Signature = $"{action.Name}({string.Join(", ", parameters.Select(p => p.Name))})";
        if (parameters.Length == 0)
        {
            Signature = $"{action.Name}()";
        }
        var function = new YarnActionFunction
        {
            YarnName = action.Name,
            DefinitionName = action.MethodIdentifierName,
            Signature = Signature,
            Parameters = parameters,
            ReturnType = InternalTypeToYarnType(action.MethodSymbol.ReturnType),
            FileName = action.SourceFileName,
        };
        functions.Add(function);
    }
    private void AddCommand(YarnAction action)
    {
        var parameters = GenerateParams(action.Parameters);
        var Signature = $"<<{action.Name} {string.Join(" ", parameters.Select(p => p.Name))}>>";
        if (parameters.Length == 0)
        {
            Signature = $"<<{action.Name}>>";
        }
        var command = new YarnActionCommand
        {
            YarnName = action.Name,
            DefinitionName = action.MethodIdentifierName,
            Signature = Signature,
            Parameters = parameters,
            FileName = action.SourceFileName,
        };
        commands.Add(command);
    }
    private YarnActionParameter[] GenerateParams(List<Parameter> parameters)
    {
        List<YarnActionParameter> parameterList = new List<YarnActionParameter>();
        foreach (var param in parameters)
        {
            var paramType = InternalTypeToYarnType(param.Type);

            var parameter = new YarnActionParameter
            {
                Name = param.Name,
                Type = paramType,
                IsParamsArray = false,
            };
            parameterList.Add(parameter);
        }
        return parameterList.ToArray();
    }
    private string InternalTypeToYarnType(ITypeSymbol symbol)
    {
        var type = "any";
        switch (symbol.SpecialType)
        {
            case SpecialType.System_Boolean:
                type = "boolean";
                break;
            case SpecialType.System_SByte:
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Decimal:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
                type = "number";
                break;
            case SpecialType.System_String:
                type = "string";
                break;
        }
        return type;
    }
}

static class EnumerableExtensions {
    public static IEnumerable<T> NonNull<T>(this IEnumerable<T?> collection, bool throwIfAnyNull = false) where T:class {
        foreach (var item in collection) {
            if (item != null) {
                yield return item;
            } else {
                if (throwIfAnyNull) {
                    throw new NullReferenceException("Collection contains a null item");
                } 
            }
        }
    }
}


static class SymbolExtensions {

    /// <summary>
    /// If the <paramref name="symbol"/> is a method symbol, returns <see langword="true"/> if the method's return type is "awaitable", but not if it's <see langword="dynamic"/>.
    /// If the <paramref name="symbol"/> is a type symbol, returns <see langword="true"/> if that type is "awaitable".
    /// An "awaitable" is any type that exposes a GetAwaiter method which returns a valid "awaiter". This GetAwaiter method may be an instance method or an extension method.
    /// </summary>
    public static bool IsAwaitableNonDynamic(this ISymbol symbol, SemanticModel semanticModel, int position)
    {
        IMethodSymbol? methodSymbol = symbol as IMethodSymbol;
        ITypeSymbol? typeSymbol = null;

        if (methodSymbol == null)
        {
            typeSymbol = symbol as ITypeSymbol;
            if (typeSymbol == null)
            {
                return false;
            }
        }
        else
        {
            if (methodSymbol.ReturnType == null)
            {
                return false;
            }
        }

        // otherwise: needs valid GetAwaiter
        var potentialGetAwaiters = semanticModel.LookupSymbols(position,
                                                               container: typeSymbol ?? methodSymbol?.ReturnType.OriginalDefinition,
                                                               name: WellKnownMemberNames.GetAwaiter,
                                                               includeReducedExtensionMethods: true);
        var getAwaiters = potentialGetAwaiters.OfType<IMethodSymbol>().Where(x => !x.Parameters.Any());
        return getAwaiters.Any(VerifyGetAwaiter);
    }

    private static bool VerifyGetAwaiter(IMethodSymbol getAwaiter)
    {
        var returnType = getAwaiter.ReturnType;
        if (returnType == null)
        {
            return false;
        }

        // bool IsCompleted { get }
        if (!returnType.GetMembers().OfType<IPropertySymbol>().Any(p => p.Name == WellKnownMemberNames.IsCompleted && p.Type.SpecialType == SpecialType.System_Boolean && p.GetMethod != null))
        {
            return false;
        }

        var methods = returnType.GetMembers().OfType<IMethodSymbol>();

        // NOTE: (vladres) The current version of C# Spec, §7.7.7.3 'Runtime evaluation of await expressions', requires that
        // NOTE: the interface method INotifyCompletion.OnCompleted or ICriticalNotifyCompletion.UnsafeOnCompleted is invoked
        // NOTE: (rather than any OnCompleted method conforming to a certain pattern).
        // NOTE: Should this code be updated to match the spec?

        // void OnCompleted(Action) 
        // Actions are delegates, so we'll just check for delegates.
        if (!methods.Any(x => x.Name == WellKnownMemberNames.OnCompleted && x.ReturnsVoid && x.Parameters.Length == 1 && x.Parameters.First().Type.TypeKind == TypeKind.Delegate))
        {
            return false;
        }

        // void GetResult() || T GetResult()
        return methods.Any(m => m.Name == WellKnownMemberNames.GetResult && !m.Parameters.Any());
    }
}
