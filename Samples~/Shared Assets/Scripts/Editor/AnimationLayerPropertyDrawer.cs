using UnityEditor;
using UnityEngine;
using System.Linq;

namespace Yarn.Unity.Samples.Editor
{

    [CustomPropertyDrawer(typeof(AnimationLayerAttribute))]
    public class AnimationLayerPropertyDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            using (new EditorGUI.PropertyScope(position, label, property))
            {
                if (property.propertyType != SerializedPropertyType.String)
                {
                    EditorGUI.HelpBox(position, "Invalid property type " + property.propertyType, MessageType.Error);
                    return;
                }

                var attribute = this.attribute as AnimationLayerAttribute
                    ?? throw new System.InvalidOperationException($"Target is not {nameof(AnimationLayerAttribute)}");
                var animator = property.serializedObject.FindProperty(attribute.AnimatorPropertyName)?.objectReferenceValue as Animator;

                if (animator == null)
                {
                    using (new EditorGUI.DisabledScope())
                    {
                        EditorGUI.Popup(position, label, 0, new GUIContent[] { });
                    }
                    return;
                }

                var layerNames = Enumerable.Range(0, animator.layerCount).Select(i => animator.GetLayerName(i)).ToList();
                var parameterContent = layerNames.Select(p => new GUIContent(p)).ToArray();

                var selectedIndex = layerNames.IndexOf(property.stringValue);

                selectedIndex = EditorGUI.Popup(position, label, selectedIndex, parameterContent);

                if (selectedIndex >= 0 && selectedIndex < layerNames.Count)
                {
                    property.stringValue = layerNames[selectedIndex];
                }
            }
        }
    }
}