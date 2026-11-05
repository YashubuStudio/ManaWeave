using UnityEngine;

namespace ManaWeave.Runtime
{
    /// <summary>The sample scene is intentionally asset-free: the world is built from its seed.</summary>
    public static class RuntimeBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Launch()
        {
            if (Object.FindFirstObjectByType<ManaWorldController>() != null) return;
            var root = new GameObject("ManaWeave / World");
            root.AddComponent<ManaWorldController>();
        }
    }
}
