using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Fablewood.EditorTools
{
    /// <summary>
    /// Tools > Fablewood > Setup Character Controller
    ///
    /// Select your character in the Hierarchy, open this window, check the animation clips,
    /// then press "Setup". It will:
    ///   - find the character's Animator (also on child objects) and check it has a Humanoid avatar
    ///   - add and size a CharacterController
    ///   - add the ThirdPersonController script
    ///   - set up a ThirdPersonCamera on the Main Camera (or create one)
    ///   - find animation clips (Kevin Iglesias Human Basic Motions) and build an Animator Controller
    /// Running it again updates the existing setup instead of creating duplicates.
    /// </summary>
    public class CharacterSetupWindow : EditorWindow
    {
        const string OutputFolder = "Assets/Fablewood/Animations";
        const string LocomotionStateName = "Locomotion";

        enum ClipSlot { Idle, Walk, Run, Jump, Fall, Land }
        enum BodyTypePreference { Any, Male, Female }

        static readonly string[] SlotLabels = { "Idle", "Walk", "Run", "Jump", "Fall (optional)", "Land (optional)" };
        static readonly string[] SlotHelp =
        {
            "Standing still.",
            "Played at walking speed.",
            "Played while sprinting (holding Shift).",
            "Played when Space is pressed. A 'jump start' clip works best when Fall and Land are set.",
            "Loops while in the air. Leave empty to skip.",
            "Played when touching the ground after a fall. Leave empty to skip.",
        };

        GameObject character;
        BodyTypePreference bodyType = BodyTypePreference.Male;
        string animationPackagePath;               // folder where the animation package was found
        List<ClipCandidate> allClips = new List<ClipCandidate>();
        readonly AnimationClip[] chosenClips = new AnimationClip[6];
        readonly List<ClipCandidate>[] candidates = new List<ClipCandidate>[6];
        Vector2 scroll;

        class ClipCandidate
        {
            public AnimationClip clip;
            public string path;
            public string label;
            public int score;
        }

        [MenuItem("Tools/Fablewood/Setup Character Controller")]
        public static void Open()
        {
            var window = GetWindow<CharacterSetupWindow>("Fablewood Character Setup");
            window.minSize = new Vector2(420, 520);
            window.Show();
        }

        void OnEnable()
        {
            PickCharacterFromSelection();
            ScanClips();
        }

        void OnSelectionChange()
        {
            PickCharacterFromSelection();
            Repaint();
        }

        void PickCharacterFromSelection()
        {
            var selected = Selection.activeGameObject;
            if (selected == null || EditorUtility.IsPersistent(selected)) return; // ignore project assets
            character = FindCharacterRoot(selected);
        }

        /// <summary>
        /// If the user clicked a child (e.g. the mesh or a bone), walk up to the character's root:
        /// the outermost prefab instance, or the highest parent that has an Animator.
        /// </summary>
        static GameObject FindCharacterRoot(GameObject selected)
        {
            var prefabRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(selected);
            if (prefabRoot != null) return prefabRoot;

            if (selected.GetComponentInChildren<Animator>(true) != null) return selected;

            var parentAnimator = selected.GetComponentInParent<Animator>(true);
            return parentAnimator != null ? parentAnimator.gameObject : selected;
        }

        // ------------------------------------------------------------------ GUI

        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            EditorGUILayout.LabelField("Fablewood Character Setup", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "1. Select your character in the Hierarchy.\n" +
                "2. Check the animation clips below (change them if they look wrong).\n" +
                "3. Click Setup, then press Play.", MessageType.None);

            EditorGUILayout.Space();
            DrawCharacterSection();

            EditorGUILayout.Space();
            DrawAnimationSection();

            EditorGUILayout.Space(12);
            DrawSetupButton();

            EditorGUILayout.EndScrollView();
        }

        void DrawCharacterSection()
        {
            EditorGUILayout.LabelField("Character", EditorStyles.boldLabel);
            character = (GameObject)EditorGUILayout.ObjectField("Character", character, typeof(GameObject), true);

            if (character == null)
            {
                EditorGUILayout.HelpBox("Select your character (e.g. Finn) in the Hierarchy.", MessageType.Info);
                return;
            }
            if (EditorUtility.IsPersistent(character))
            {
                EditorGUILayout.HelpBox("This is an asset in the Project window. Drag it into the scene first, then select it in the Hierarchy.", MessageType.Error);
                return;
            }

            var animator = character.GetComponentInChildren<Animator>(true);
            if (animator == null)
            {
                EditorGUILayout.HelpBox("No Animator found on this object or its children. Is this the right object?", MessageType.Error);
                return;
            }

            Avatar avatar = FindAvatar(animator);
            if (avatar == null || !avatar.isValid || !avatar.isHuman)
            {
                EditorGUILayout.HelpBox(
                    "This character does not have a valid Humanoid avatar.\n" +
                    "Fix: select the character's model file in the Project window, open the Rig tab in the Inspector, " +
                    "set Animation Type to 'Humanoid' and click Apply.", MessageType.Error);
                return;
            }

            string where = animator.gameObject == character ? "on the character" : $"on child '{animator.gameObject.name}'";
            EditorGUILayout.HelpBox($"Animator found {where}. Humanoid avatar: {avatar.name}", MessageType.None);

            if (!HasGroundBelow(character))
            {
                EditorGUILayout.HelpBox("There is no ground (collider) under the character, so it will fall forever when you press Play.", MessageType.Warning);
                if (GUILayout.Button("Create Ground Plane"))
                    CreateGroundPlane();
            }
        }

        void DrawAnimationSection()
        {
            EditorGUILayout.LabelField("Animation Clips", EditorStyles.boldLabel);

            if (string.IsNullOrEmpty(animationPackagePath))
                EditorGUILayout.HelpBox(
                    "Could not find the 'Kevin Iglesias / Human Basic Motions' package in your Assets folder. " +
                    "Import it from the Asset Store / Package Manager, then click Re-scan. " +
                    "You can also pick any humanoid clips manually below.", MessageType.Warning);
            else
                EditorGUILayout.HelpBox("Animations found in: " + animationPackagePath, MessageType.None);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                bodyType = (BodyTypePreference)EditorGUILayout.EnumPopup(
                    new GUIContent("Prefer clips for", "Kevin Iglesias includes male and female versions of each animation."), bodyType);
                if (EditorGUI.EndChangeCheck()) ScanClips();
                if (GUILayout.Button("Re-scan", GUILayout.Width(70))) ScanClips();
            }

            EditorGUILayout.Space(4);
            for (int i = 0; i < chosenClips.Length; i++)
                DrawClipSlot((ClipSlot)i);
        }

        void DrawClipSlot(ClipSlot slot)
        {
            int i = (int)slot;
            var list = candidates[i] ?? new List<ClipCandidate>();

            EditorGUILayout.LabelField(new GUIContent(SlotLabels[i], SlotHelp[i]), EditorStyles.miniBoldLabel);
            EditorGUI.indentLevel++;

            // Dropdown of likely matches (best first).
            if (list.Count > 0)
            {
                var options = new List<string> { "(choose a suggested clip)" };
                options.AddRange(list.Select(c => c.label));
                int current = list.FindIndex(c => c.clip == chosenClips[i]) + 1;
                int picked = EditorGUILayout.Popup("Suggestions", current, options.ToArray());
                if (picked != current && picked > 0) chosenClips[i] = list[picked - 1].clip;
            }

            // Object field so any clip in the project can be used.
            chosenClips[i] = (AnimationClip)EditorGUILayout.ObjectField("Clip", chosenClips[i], typeof(AnimationClip), false);

            var chosen = chosenClips[i];
            if (chosen == null && slot <= ClipSlot.Jump)
                EditorGUILayout.HelpBox("Required: please choose a clip.", MessageType.Error);
            else if (chosen != null && !chosen.humanMotion)
                EditorGUILayout.HelpBox("This clip is not a Humanoid animation and may not play on your character. " +
                                        "Set its Rig > Animation Type to Humanoid.", MessageType.Warning);
            else if (chosen != null && IsRootMotionClip(AssetDatabase.GetAssetPath(chosen), chosen.name))
                EditorGUILayout.HelpBox("This looks like a root-motion clip. An in-place clip is recommended, " +
                                        "because movement is handled by the script.", MessageType.Info);
            else if (list.Count > 1 && chosen == list[0].clip && list[0].score - list[1].score < 10)
                EditorGUILayout.HelpBox("Several clips matched. Please double-check this choice.", MessageType.None);

            EditorGUI.indentLevel--;
            EditorGUILayout.Space(2);
        }

        void DrawSetupButton()
        {
            string problem = GetBlockingProblem();
            using (new EditorGUI.DisabledScope(problem != null))
            {
                if (GUILayout.Button("Setup", GUILayout.Height(36)))
                    RunSetup();
            }
            if (problem != null)
                EditorGUILayout.HelpBox(problem, MessageType.Info);
        }

        string GetBlockingProblem()
        {
            if (character == null || EditorUtility.IsPersistent(character)) return "Select a character in the scene to enable Setup.";
            var animator = character.GetComponentInChildren<Animator>(true);
            if (animator == null) return "The character needs an Animator.";
            var avatar = FindAvatar(animator);
            if (avatar == null || !avatar.isHuman || !avatar.isValid) return "The character needs a Humanoid avatar.";
            for (int i = 0; i <= (int)ClipSlot.Jump; i++)
                if (chosenClips[i] == null) return $"Choose a clip for {SlotLabels[i]}.";
            return null;
        }

        // ------------------------------------------------------------------ Clip detection

        void ScanClips()
        {
            animationPackagePath = FindAnimationPackageFolder();
            allClips = FindHumanoidClips(animationPackagePath);

            // If the package wasn't found (or has no clips), fall back to every clip in the project.
            if (allClips.Count == 0) allClips = FindHumanoidClips(null);

            for (int i = 0; i < chosenClips.Length; i++)
            {
                var slot = (ClipSlot)i;
                var scored = new List<ClipCandidate>();
                foreach (var c in allClips)
                {
                    int score = ScoreClip(slot, c);
                    if (score > 0) scored.Add(new ClipCandidate { clip = c.clip, path = c.path, label = c.label, score = score });
                }
                candidates[i] = scored.OrderByDescending(c => c.score).ThenBy(c => c.label).Take(25).ToList();
            }

            // Pick best matches. Jump is chosen last so it can depend on whether Fall/Land were found.
            foreach (var slot in new[] { ClipSlot.Idle, ClipSlot.Walk, ClipSlot.Run, ClipSlot.Fall, ClipSlot.Land })
                chosenClips[(int)slot] = candidates[(int)slot].Count > 0 ? candidates[(int)slot][0].clip : null;

            // With Fall + Land available, a short "jump start" clip looks best; otherwise use a full jump clip.
            bool splitJump = chosenClips[(int)ClipSlot.Fall] != null && chosenClips[(int)ClipSlot.Land] != null;
            var jumpList = candidates[(int)ClipSlot.Jump];
            foreach (var c in jumpList)
            {
                string n = c.clip.name.ToLowerInvariant();
                bool isStart = n.Contains("begin") || n.Contains("start") || n.Contains("takeoff");
                if (!splitJump && isStart) c.score -= 20;
            }
            candidates[(int)ClipSlot.Jump] = jumpList.OrderByDescending(c => c.score).ThenBy(c => c.label).ToList();
            chosenClips[(int)ClipSlot.Jump] = jumpList.Count > 0 ? candidates[(int)ClipSlot.Jump][0].clip : null;

            Repaint();
        }

        /// <summary>Looks for a folder belonging to Kevin Iglesias' Human Basic Motions package.</summary>
        static string FindAnimationPackageFolder()
        {
            // Look at every folder under Assets and pick the best-matching one.
            string best = null;
            int bestScore = 0;
            foreach (string path in GetAllFolders("Assets"))
            {
                string name = Path.GetFileName(path).ToLowerInvariant();
                int score = 0;
                if (name.Contains("human basic motions") || name.Contains("humanbasicmotions")) score = 3;
                else if (name.Contains("kevin iglesias") || name.Contains("keviniglesias")) score = 2;
                else if (name == "human animations") score = 1;

                // Prefer the shallowest folder (the package root).
                if (score > bestScore || (score == bestScore && score > 0 && path.Length < best.Length))
                {
                    best = path;
                    bestScore = score;
                }
            }

            // "Human Basic Motions" might only be the demo-scene folder; use its package root if possible.
            if (best != null && bestScore == 3)
            {
                string p = best;
                while (!string.IsNullOrEmpty(p) && p != "Assets")
                {
                    string n = Path.GetFileName(p).ToLowerInvariant();
                    if (n.Contains("kevin iglesias")) { best = p; break; }
                    p = Path.GetDirectoryName(p)?.Replace('\\', '/');
                }
            }
            return best;
        }

        static IEnumerable<string> GetAllFolders(string root)
        {
            foreach (string sub in AssetDatabase.GetSubFolders(root))
            {
                yield return sub;
                foreach (string deeper in GetAllFolders(sub)) yield return deeper;
            }
        }

        static List<ClipCandidate> FindHumanoidClips(string folder)
        {
            var result = new List<ClipCandidate>();
            string[] searchIn = string.IsNullOrEmpty(folder) ? new[] { "Assets" } : new[] { folder };
            var seenPaths = new HashSet<string>();

            foreach (string guid in AssetDatabase.FindAssets("t:AnimationClip", searchIn))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!seenPaths.Add(path)) continue;

                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (!(asset is AnimationClip clip)) continue;
                    if (clip.name.StartsWith("__preview__")) continue;
                    if (!clip.humanMotion) continue; // must work with a humanoid rig

                    string folderName = Path.GetFileName(Path.GetDirectoryName(path));
                    result.Add(new ClipCandidate
                    {
                        clip = clip,
                        path = path,
                        label = $"{clip.name}   ({folderName})",
                    });
                }
            }
            return result;
        }

        static bool IsRootMotionClip(string path, string clipName)
        {
            string p = (path + "/" + clipName).ToLowerInvariant();
            return p.Contains("[rm]") || p.Contains("rootmotion") || p.Contains("root motion") || p.Contains("_rm");
        }

        static readonly string[] SideDirections = { "left", "right", "backward", "back", "strafe" };

        /// <summary>
        /// Gives each clip a score for a slot based on its name. Higher = better match. 0 or less = not a match.
        /// </summary>
        int ScoreClip(ClipSlot slot, ClipCandidate c)
        {
            string name = c.clip.name.ToLowerInvariant();
            string path = c.path.ToLowerInvariant();
            int score = 0;

            switch (slot)
            {
                case ClipSlot.Idle:
                    if (!name.Contains("idle")) return 0;
                    score = 100;
                    // "Idle01-Idle02" style clips are transitions between idles, not loops.
                    if (name.IndexOf("idle") != name.LastIndexOf("idle")) score -= 40;
                    if (name.Contains("01") || name.EndsWith("idle")) score += 5;
                    if (!c.clip.isLooping) score -= 15;
                    break;

                case ClipSlot.Walk:
                case ClipSlot.Run:
                    string keyword = slot == ClipSlot.Walk ? "walk" : "run";
                    if (name.Contains(keyword)) score = 100;
                    else if (slot == ClipSlot.Run && name.Contains("sprint")) score = 70;
                    else return 0;
                    if (name.Contains("forward") || name.Contains("fwd")) score += 20;
                    foreach (var dir in SideDirections)
                        if (name.Contains(dir)) score -= 30;
                    if (!c.clip.isLooping) score -= 15;
                    break;

                case ClipSlot.Jump:
                    if (!name.Contains("jump")) return 0;
                    score = 100;
                    if (name.Contains("land") || name.Contains("fall") || name.Contains("loop") || name.Contains("air")) score -= 60;
                    if (name.Contains("begin") || name.Contains("start") || name.Contains("takeoff")) score += 10;
                    break;

                case ClipSlot.Fall:
                    if (name.Contains("fall")) score = 100;
                    else if (name.Contains("jump") && (name.Contains("loop") || name.Contains("air"))) score = 80;
                    else return 0;
                    break;

                case ClipSlot.Land:
                    if (!name.Contains("land")) return 0;
                    score = 100;
                    break;
            }

            // Prefer in-place clips: the script moves the character, not the animation.
            if (IsRootMotionClip(path, name)) score -= 25;

            // Prefer the chosen body type (Kevin Iglesias names clips HumanM@... / HumanF@...).
            var body = GetBodyType(path, name);
            if (bodyType != BodyTypePreference.Any && body != BodyTypePreference.Any)
                score += body == bodyType ? 8 : -8;

            return Mathf.Max(score, 1);
        }

        static BodyTypePreference GetBodyType(string path, string name)
        {
            if (path.Contains("/female/") || name.StartsWith("humanf@")) return BodyTypePreference.Female;
            if (path.Contains("/male/") || name.StartsWith("humanm@")) return BodyTypePreference.Male;
            return BodyTypePreference.Any;
        }

        // ------------------------------------------------------------------ Setup

        void RunSetup()
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Fablewood Character Setup");
            int undoGroup = Undo.GetCurrentGroup();

            try
            {
                var animator = character.GetComponentInChildren<Animator>(true);
                Avatar avatar = FindAvatar(animator);

                // 1. Animator: correct avatar, no root motion.
                Undo.RecordObject(animator, "Configure Animator");
                animator.avatar = avatar;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                // 2. CharacterController sized to the model.
                var cc = character.GetComponent<CharacterController>();
                if (cc == null) cc = Undo.AddComponent<CharacterController>(character);
                FitCharacterController(cc, character, animator);

                // 3. Movement script (added once).
                var mover = character.GetComponent<ThirdPersonController>();
                if (mover == null) mover = Undo.AddComponent<ThirdPersonController>(character);
                Undo.RecordObject(mover, "Configure ThirdPersonController");
                mover.animator = animator;

                // 4. Camera.
                var cam = SetupCamera(character, cc);
                mover.cameraTransform = cam.transform;
                EditorUtility.SetDirty(mover);

                // 5-7. Animator Controller built from the chosen clips and assigned to the character.
                var controller = BuildAnimatorController(character.name, mover);
                Undo.RecordObject(animator, "Assign Animator Controller");
                animator.runtimeAnimatorController = controller;
                EditorUtility.SetDirty(animator);

                Undo.CollapseUndoOperations(undoGroup);
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(character.scene);

                Debug.Log($"[Fablewood] '{character.name}' is ready! Animator Controller: {AssetDatabase.GetAssetPath(controller)}. " +
                          "Press Play and use WASD, mouse, Shift and Space. (Remember to save the scene.)", character);
                EditorUtility.DisplayDialog("Fablewood Setup",
                    $"'{character.name}' is set up!\n\nPress Play and use:\n  WASD - move\n  Mouse - look around\n  Shift - sprint\n  Space - jump\n  Esc - free the mouse cursor\n\nDon't forget to save your scene (Ctrl+S).",
                    "Great!");
            }
            catch (System.Exception e)
            {
                Debug.LogError("[Fablewood] Setup failed: " + e.Message + "\n" + e.StackTrace);
                EditorUtility.DisplayDialog("Fablewood Setup", "Setup failed:\n" + e.Message + "\n\nSee the Console for details.", "OK");
            }
        }

        /// <summary>Finds the Humanoid avatar: the one on the Animator, or the one inside the model file.</summary>
        static Avatar FindAvatar(Animator animator)
        {
            if (animator.avatar != null) return animator.avatar;

            // The Animator might be missing its avatar; look inside the source model (FBX).
            var source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(animator.gameObject);
            string path = source != null ? AssetDatabase.GetAssetPath(source) : null;
            if (string.IsNullOrEmpty(path)) return null;
            return AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault(a => a.isHuman);
        }

        /// <summary>Sizes the capsule to the character's visible mesh.</summary>
        static void FitCharacterController(CharacterController cc, GameObject root, Animator animator)
        {
            Undo.RecordObject(cc, "Fit CharacterController");

            float scale = Mathf.Max(root.transform.lossyScale.y, 0.0001f);
            float height = 1.8f;
            float bottom = 0f;
            bool humanoid = animator.avatar != null && animator.avatar.isHuman;

            // Measure the real mesh (renderer.bounds is padded and made the character float).
            if (TryMeasureMesh(root, out float minY, out float maxY))
            {
                // Humanoid animations always place the feet at the character's pivot (its origin),
                // so the capsule should start exactly there, whatever the mesh's rest pose says.
                bottom = humanoid ? 0f : minY / scale;
                height = maxY / scale - bottom;
            }
            else if (humanoid && animator.GetBoneTransform(HumanBodyBones.Head) != null)
            {
                // No mesh? Use the head bone: the head is roughly 90% of a person's height.
                float headY = animator.GetBoneTransform(HumanBodyBones.Head).position.y - root.transform.position.y;
                height = headY / scale / 0.9f;
            }

            if (height < 0.1f || float.IsNaN(height))
            {
                Debug.LogWarning("[Fablewood] Couldn't measure the character, using a default 1.8m capsule.");
                height = 1.8f;
                bottom = 0f;
            }

            float radius = Mathf.Clamp(height * 0.15f, 0.1f, height * 0.5f);
            float skinWidth = Mathf.Max(0.01f, radius * 0.1f);
            cc.height = height;
            cc.radius = radius;
            cc.skinWidth = skinWidth;
            // The CharacterController rests "Skin Width" above the ground, so raise the capsule
            // by that much. That way the feet (at the pivot) touch the ground instead of hovering.
            cc.center = new Vector3(0f, bottom + height * 0.5f + skinWidth, 0f);
            cc.stepOffset = Mathf.Min(0.3f, height * 0.2f);
            cc.slopeLimit = 45f;
            cc.minMoveDistance = 0f;
            EditorUtility.SetDirty(cc);
        }

        /// <summary>
        /// Finds the lowest and highest point of the character's actual mesh vertices,
        /// measured upward from the character's pivot (in world units).
        /// </summary>
        static bool TryMeasureMesh(GameObject root, out float minY, out float maxY)
        {
            minY = float.MaxValue;
            maxY = float.MinValue;
            Vector3 rootPos = root.transform.position;
            var baked = new Mesh();

            try
            {
                foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    if (smr.sharedMesh == null) continue;
                    // BakeMesh gives the vertices in the pose the character is in right now.
                    smr.BakeMesh(baked, true);
                    var toWorld = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                    foreach (var v in baked.vertices)
                    {
                        float y = toWorld.MultiplyPoint3x4(v).y - rootPos.y;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }

                foreach (var mf in root.GetComponentsInChildren<MeshFilter>())
                {
                    if (mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                    var toWorld = mf.transform.localToWorldMatrix;
                    foreach (var v in mf.sharedMesh.vertices)
                    {
                        float y = toWorld.MultiplyPoint3x4(v).y - rootPos.y;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(baked);
            }

            return maxY > minY;
        }

        /// <summary>Uses an existing ThirdPersonCamera or the Main Camera; only creates a camera if there is none.</summary>
        static ThirdPersonCamera SetupCamera(GameObject target, CharacterController cc)
        {
            var orbit = Object.FindFirstObjectByType<ThirdPersonCamera>(FindObjectsInactive.Include);
            if (orbit == null)
            {
                Camera cam = Camera.main;
                if (cam == null)
                {
                    var camGO = new GameObject("Main Camera");
                    Undo.RegisterCreatedObjectUndo(camGO, "Create Camera");
                    camGO.tag = "MainCamera";
                    cam = Undo.AddComponent<Camera>(camGO);
                    if (Object.FindFirstObjectByType<AudioListener>() == null)
                        Undo.AddComponent<AudioListener>(camGO);
                }
                orbit = Undo.AddComponent<ThirdPersonCamera>(cam.gameObject);
            }

            Undo.RecordObject(orbit, "Configure ThirdPersonCamera");
            orbit.target = target.transform;
            float scale = target.transform.lossyScale.y;
            float height = cc.height * scale;
            orbit.targetOffset = new Vector3(0f, (cc.center.y - cc.height * 0.5f) * scale + height * 0.85f, 0f);
            orbit.distance = Mathf.Clamp(height * 2.2f, 2f, 8f);
            orbit.minDistance = Mathf.Min(orbit.minDistance, orbit.distance);
            orbit.maxDistance = Mathf.Max(orbit.maxDistance, orbit.distance);
            EditorUtility.SetDirty(orbit);

            // Put the camera behind the character so the first frame already looks right.
            Undo.RecordObject(orbit.transform, "Position Camera");
            Quaternion rot = Quaternion.Euler(15f, target.transform.eulerAngles.y, 0f);
            Vector3 focus = target.transform.position + orbit.targetOffset;
            orbit.transform.SetPositionAndRotation(focus + rot * Vector3.back * orbit.distance, rot);

            return orbit;
        }

        /// <summary>
        /// Creates (or rebuilds) the Animator Controller:
        ///   Parameters: Speed (float), IsGrounded (bool), Jump (trigger)
        ///   Locomotion (1D blend tree: Idle / Walk / Run) -> Jump -> Fall -> Land -> Locomotion
        /// </summary>
        AnimatorController BuildAnimatorController(string characterName, ThirdPersonController mover)
        {
            EnsureFolder(OutputFolder);
            string safeName = string.Join("_", characterName.Split(Path.GetInvalidFileNameChars()));
            string path = $"{OutputFolder}/{safeName}_AnimatorController.controller";

            // Reuse the existing asset if setup was run before (keeps references intact).
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (controller == null)
                controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            else
                ClearController(controller, path);

            controller.AddParameter(ThirdPersonController.SpeedParam, AnimatorControllerParameterType.Float);
            controller.AddParameter(ThirdPersonController.GroundedParam, AnimatorControllerParameterType.Bool);
            controller.AddParameter(ThirdPersonController.JumpParam, AnimatorControllerParameterType.Trigger);
            // Start grounded so the character doesn't flash into the fall animation.
            var parameters = controller.parameters;
            foreach (var p in parameters)
                if (p.name == ThirdPersonController.GroundedParam) p.defaultBool = true;
            controller.parameters = parameters;

            var sm = controller.layers[0].stateMachine;

            // --- Locomotion blend tree. Thresholds match the script's speeds (meters per second).
            var locomotion = controller.CreateBlendTreeInController(LocomotionStateName, out BlendTree tree, 0);
            tree.name = "Locomotion Blend Tree";
            tree.blendType = BlendTreeType.Simple1D;
            tree.blendParameter = ThirdPersonController.SpeedParam;
            tree.useAutomaticThresholds = false;
            tree.AddChild(chosenClips[(int)ClipSlot.Idle], 0f);
            tree.AddChild(chosenClips[(int)ClipSlot.Walk], mover.walkSpeed);
            tree.AddChild(chosenClips[(int)ClipSlot.Run], mover.sprintSpeed);
            sm.defaultState = locomotion;
            // Foot IK keeps the feet planted where the animation intended, even though the
            // animation was made for a character with different proportions.
            locomotion.iKOnFeet = true;
            SetStatePosition(sm, locomotion, new Vector3(300, 100));

            // --- Jump
            var jump = sm.AddState("Jump");
            jump.motion = chosenClips[(int)ClipSlot.Jump];
            SetStatePosition(sm, jump, new Vector3(300, 250));

            var toJump = locomotion.AddTransition(jump);
            toJump.hasExitTime = false;
            toJump.duration = 0.1f;
            toJump.AddCondition(AnimatorConditionMode.If, 0, ThirdPersonController.JumpParam);

            AnimationClip fallClip = chosenClips[(int)ClipSlot.Fall];
            AnimationClip landClip = chosenClips[(int)ClipSlot.Land];

            if (fallClip != null)
            {
                // --- Fall (loops while in the air)
                var fall = sm.AddState("Fall");
                fall.motion = fallClip;
                SetStatePosition(sm, fall, new Vector3(550, 250));

                var jumpToFall = jump.AddTransition(fall);
                jumpToFall.hasExitTime = true;
                jumpToFall.exitTime = 0.85f;
                jumpToFall.duration = 0.15f;

                // Walking off a ledge also starts falling.
                var locoToFall = locomotion.AddTransition(fall);
                locoToFall.hasExitTime = false;
                locoToFall.duration = 0.2f;
                locoToFall.AddCondition(AnimatorConditionMode.IfNot, 0, ThirdPersonController.GroundedParam);

                AnimatorState afterAir = locomotion;
                if (landClip != null)
                {
                    // --- Land (only when not moving much; when running we blend straight back to locomotion)
                    var land = sm.AddState("Land");
                    land.motion = landClip;
                    land.iKOnFeet = true;
                    SetStatePosition(sm, land, new Vector3(550, 100));

                    AddLandingTransitions(jump, land, locomotion, mover);
                    AddLandingTransitions(fall, land, locomotion, mover);

                    var landToLoco = land.AddTransition(locomotion);
                    landToLoco.hasExitTime = true;
                    landToLoco.exitTime = 0.6f;
                    landToLoco.duration = 0.2f;

                    // Start moving again straight away if the player gives input.
                    var landToLocoMove = land.AddTransition(locomotion);
                    landToLocoMove.hasExitTime = false;
                    landToLocoMove.duration = 0.2f;
                    landToLocoMove.AddCondition(AnimatorConditionMode.Greater, mover.walkSpeed * 0.5f, ThirdPersonController.SpeedParam);
                }
                else
                {
                    AddGroundedTransition(jump, afterAir);
                    AddGroundedTransition(fall, afterAir);
                }
            }
            else
            {
                // No fall clip: go from Jump straight back to Locomotion when we land.
                AddGroundedTransition(jump, locomotion);
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            return controller;
        }

        static void AddLandingTransitions(AnimatorState from, AnimatorState land, AnimatorState locomotion, ThirdPersonController mover)
        {
            float movingThreshold = mover.walkSpeed * 0.5f;

            var toLocomotion = from.AddTransition(locomotion);
            toLocomotion.hasExitTime = false;
            toLocomotion.duration = 0.15f;
            toLocomotion.AddCondition(AnimatorConditionMode.If, 0, ThirdPersonController.GroundedParam);
            toLocomotion.AddCondition(AnimatorConditionMode.Greater, movingThreshold, ThirdPersonController.SpeedParam);

            var toLand = from.AddTransition(land);
            toLand.hasExitTime = false;
            toLand.duration = 0.1f;
            toLand.AddCondition(AnimatorConditionMode.If, 0, ThirdPersonController.GroundedParam);
            toLand.AddCondition(AnimatorConditionMode.Less, movingThreshold, ThirdPersonController.SpeedParam);
        }

        static void AddGroundedTransition(AnimatorState from, AnimatorState to)
        {
            var t = from.AddTransition(to);
            t.hasExitTime = false;
            t.duration = 0.15f;
            t.AddCondition(AnimatorConditionMode.If, 0, ThirdPersonController.GroundedParam);
        }

        /// <summary>Empties an existing controller so it can be rebuilt without leaving junk inside the asset.</summary>
        static void ClearController(AnimatorController controller, string path)
        {
            while (controller.layers.Length > 1) controller.RemoveLayer(controller.layers.Length - 1);

            var sm = controller.layers[0].stateMachine;
            foreach (var t in sm.anyStateTransitions) sm.RemoveAnyStateTransition(t);
            foreach (var t in sm.entryTransitions) sm.RemoveEntryTransition(t);
            foreach (var child in sm.stateMachines) sm.RemoveStateMachine(child.stateMachine);
            foreach (var child in sm.states) sm.RemoveState(child.state);

            while (controller.parameters.Length > 0) controller.RemoveParameter(0);

            // Blend trees are stored inside the controller asset and aren't removed with their state.
            foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (obj is BlendTree)
                {
                    AssetDatabase.RemoveObjectFromAsset(obj);
                    Object.DestroyImmediate(obj, true);
                }
            }
        }

        static void SetStatePosition(AnimatorStateMachine sm, AnimatorState state, Vector3 position)
        {
            var states = sm.states;
            for (int i = 0; i < states.Length; i++)
                if (states[i].state == state) states[i].position = position;
            sm.states = states;
        }

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        // ------------------------------------------------------------------ Ground helpers

        static bool HasGroundBelow(GameObject go)
        {
            Vector3 origin = go.transform.position + Vector3.up * 1f;
            foreach (var hit in Physics.RaycastAll(origin, Vector3.down, 100f, ~0, QueryTriggerInteraction.Ignore))
                if (!hit.transform.IsChildOf(go.transform)) return true;

            // Terrains in edit mode are always raycastable, but check them directly too just in case.
            foreach (var terrain in Terrain.activeTerrains)
            {
                var b = terrain.terrainData != null ? new Bounds(terrain.transform.position + terrain.terrainData.size * 0.5f, terrain.terrainData.size) : default;
                Vector3 p = go.transform.position;
                if (p.x >= b.min.x && p.x <= b.max.x && p.z >= b.min.z && p.z <= b.max.z) return true;
            }
            return false;
        }

        void CreateGroundPlane()
        {
            var plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
            plane.name = "Ground";
            plane.transform.position = new Vector3(character.transform.position.x, character.transform.position.y, character.transform.position.z);
            plane.transform.localScale = new Vector3(10f, 1f, 10f); // 100 x 100 meters
            Undo.RegisterCreatedObjectUndo(plane, "Create Ground Plane");
        }
    }
}
