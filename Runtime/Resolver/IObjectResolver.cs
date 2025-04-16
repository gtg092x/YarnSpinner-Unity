using UnityEngine;
namespace Yarn.Unity
{
    public interface IObjectResolver
    {
        GameObject Resolve(string path);
    }
}
