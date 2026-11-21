using UnityEditor;
using UnityEngine;

namespace Fablewood.EditorTools
{
    /// <summary>
    /// The two free Asset Store packs this project uses are not included in the Git repository
    /// (their licenses don't allow it). Asset Store packs can't be listed in Packages/manifest.json
    /// either, so this window tells you which ones are missing and where to get them.
    ///
    /// It opens automatically once per session if something is missing.
    /// You can also open it any time from Tools > Fablewood > Required Assets.
    /// </summary>
    [InitializeOnLoad]
    public class RequiredAssetsCheck : EditorWindow
    {
        const string SessionKey = "Fablewood.RequiredAssetsCheck.Shown";

        struct RequiredPack
        {
            public string name;
            public string description;
            public string folderToCheck; // a folder that exists once the pack is imported
            public string url;
        }

        static readonly RequiredPack[] Packs =
        {
            new RequiredPack
            {
                name = "Human Basic Motions FREE (Kevin Iglesias)",
                description = "Idle, walk, run and jump animations for the character.",
                folderToCheck = "Assets/Kevin Iglesias",
                url = "https://assetstore.unity.com/packages/3d/animations/basic-motions-free-154271",
            },
            new RequiredPack
            {
                name = "Fantasy Kingdom in Unity 6 | URP (Unity Technologies)",
                description = "The fantasy village environment and Demo scene. Large download (~1 GB).",
                folderToCheck = "Assets/Art/Meshes/Compositions",
                url = "https://assetstore.unity.com/packages/slug/298128",
            },
        };

        static RequiredAssetsCheck()
        {
            // Wait until the editor has finished loading before checking.
            EditorApplication.delayCall += () =>
            {
                if (SessionState.GetBool(SessionKey, false)) return;
                SessionState.SetBool(SessionKey, true);
                if (AnyMissing()) Open();
            };
        }

        [MenuItem("Tools/Fablewood/Required Assets")]
        public static void Open()
        {
            var window = GetWindow<RequiredAssetsCheck>(true, "Fablewood - Required Assets");
            window.minSize = new Vector2(460, 330);
            window.Show();
        }

        static bool IsInstalled(RequiredPack pack) => AssetDatabase.IsValidFolder(pack.folderToCheck);

        static bool AnyMissing()
        {
            foreach (var pack in Packs)
                if (!IsInstalled(pack)) return true;
            return false;
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("Free Asset Store packs used by Fablewood", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "These packs aren't included in the GitHub repository. Add them to your Unity account " +
                "(both are free), then import them in Window > Package Manager > My Assets.\n\n" +
                "When Fantasy Kingdom asks to overwrite Project Settings, click Import - " +
                "it needs its own quality and render settings.", MessageType.Info);

            EditorGUILayout.Space();
            foreach (var pack in Packs)
            {
                bool installed = IsInstalled(pack);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField((installed ? "✔ " : "✖ ") + pack.name, EditorStyles.boldLabel);
                    EditorGUILayout.LabelField(pack.description, EditorStyles.wordWrappedLabel);
                    if (!installed && GUILayout.Button("Open in Asset Store"))
                        Application.OpenURL(pack.url);
                }
            }

            EditorGUILayout.Space();
            if (!AnyMissing())
                EditorGUILayout.HelpBox("Everything is installed. See README.md for the next steps.", MessageType.None);
            if (GUILayout.Button("Close")) Close();
        }

        // Refresh the checkmarks after a pack finishes importing.
        void OnProjectChange() => Repaint();
    }
}
