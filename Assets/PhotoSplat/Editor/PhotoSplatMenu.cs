using UnityEditor;
using UnityEngine;

namespace PhotoSplat.EditorTools
{
    /// <summary>Tools > PhotoSplat > Add To Scene: one click instead of wiring the component by hand.</summary>
    static class PhotoSplatMenu
    {
        const string DataFolder = "Assets/PhotoSplat/Data";

        [MenuItem("Tools/PhotoSplat/Add To Scene")]
        static void AddToScene()
        {
            var existing = Object.FindFirstObjectByType<PhotoSplatEffect>();
            if (existing != null)
            {
                Selection.activeObject = existing.gameObject;
                Debug.Log("[PhotoSplat] The scene already has a PhotoSplat object; selected it.");
                return;
            }

            var go = new GameObject("PhotoSplat");
            Undo.RegisterCreatedObjectUndo(go, "Add PhotoSplat");
            go.AddComponent<PinchInput>();
            var fx = go.AddComponent<PhotoSplatEffect>();
            fx.splatShader = Shader.Find("PhotoSplat/Splat");
            fx.photoShader = Shader.Find("Universal Render Pipeline/Unlit");

            // first .bytes and first image in Assets/PhotoSplat/Data
            foreach (var guid in AssetDatabase.FindAssets("t:TextAsset", new[] { DataFolder }))
            {
                fx.splatData = AssetDatabase.LoadAssetAtPath<TextAsset>(AssetDatabase.GUIDToAssetPath(guid));
                if (fx.splatData != null) break;
            }
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { DataFolder }))
            {
                fx.photo = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(guid));
                if (fx.photo != null) break;
            }

            Selection.activeObject = go;
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);
            Debug.Log($"[PhotoSplat] Added. Splat data: {(fx.splatData ? fx.splatData.name : "NONE")}, photo: {(fx.photo ? fx.photo.name : "NONE")}. Save the scene (Cmd+S).");
        }
    }
}
