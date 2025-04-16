using UnityEngine;
namespace Yarn.Unity
{

    public class FindGameObjectBehaviour : GameObjectResolverBehaviour
    {
        public override GameObject Resolve(string path)
        {
            return GameObject.Find(path);
        }
    }
}
