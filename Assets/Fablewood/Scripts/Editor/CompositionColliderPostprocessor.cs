using UnityEditor;
using UnityEngine;

namespace Fablewood.EditorTools
{
    /// <summary>
    /// The Fantasy Kingdom "Composition" models each contain several versions of the same mesh:
    /// LOD0 (detailed), LOD1, LOD2 (simpler, used far away) and an "_SP" proxy.
    ///
    /// Out of the box these models have no collision, so the character falls through stairs,
    /// floors and walls. This script runs whenever a composition model is imported and:
    ///   1. turns on "Generate Colliders" (so this also works right after importing the pack), and
    ///   2. keeps only the collider on the detailed LOD0 mesh, instead of four overlapping ones.
    /// </summary>
    public class CompositionColliderPostprocessor : AssetPostprocessor
    {
        const string CompositionFolder = "/Art/Meshes/Compositions/";

        void OnPreprocessModel()
        {
            if (!assetPath.Replace('\\', '/').Contains(CompositionFolder)) return;

            var importer = (ModelImporter)assetImporter;
            if (!importer.addCollider) importer.addCollider = true;
        }

        void OnPostprocessModel(GameObject root)
        {
            if (!assetPath.Replace('\\', '/').Contains(CompositionFolder)) return;

            var colliders = root.GetComponentsInChildren<MeshCollider>(true);

            // Only clean up if this model really has a LOD0 mesh with a collider (otherwise leave it alone).
            bool hasLod0Collider = false;
            foreach (var c in colliders)
                if (c.gameObject.name.EndsWith("_LOD0")) hasLod0Collider = true;
            if (!hasLod0Collider) return;

            foreach (var c in colliders)
                if (!c.gameObject.name.EndsWith("_LOD0"))
                    Object.DestroyImmediate(c, true);
        }
    }
}
