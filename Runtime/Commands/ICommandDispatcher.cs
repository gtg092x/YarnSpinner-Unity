/*
Yarn Spinner is licensed to you under the terms found in the file LICENSE.md.
*/

using System;
using System.Collections.Generic;
using UnityEngine;

#nullable enable

namespace Yarn.Unity
{
    public interface ICommandDispatcher : IActionRegistration
    {
        CommandDispatchResult DispatchCommand(string command, MonoBehaviour coroutineHost);

        void SetupForProject(YarnProject yarnProject);

        IEnumerable<ICommand> Commands { get; }
    }
}
