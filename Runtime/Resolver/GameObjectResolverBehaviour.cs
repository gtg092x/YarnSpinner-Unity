using UnityEngine;
namespace Yarn.Unity
{
    public abstract class GameObjectResolverBehaviour : MonoBehaviour, IObjectResolver
    {
        public abstract GameObject Resolve(string path);
    }
}
