using UnityEngine;
namespace Yarn.Unity
{

    public class FindGameObjectResolver : IObjectResolver
    {
        public GameObject Resolve(string path)
        {
            return GameObject.Find(path);
        }
    }
}
