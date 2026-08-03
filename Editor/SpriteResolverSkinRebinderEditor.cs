using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.U2D;
using UnityEngine.U2D.Animation;

[CustomEditor(typeof(SpriteResolverSkinRebinder))]
public sealed class SpriteResolverSkinRebinderEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space(8f);
        if (GUILayout.Button("立即按当前精灵重绑骨骼"))
        {
            foreach (UnityEngine.Object selectedTarget in targets)
            {
                if (selectedTarget is SpriteResolverSkinRebinder rebinder)
                {
                    Undo.RecordObject(rebinder, "Rebind SpriteSkin");
                    rebinder.RebindNow(true);
                    EditorUtility.SetDirty(rebinder);
                }
            }
        }

        if (GUILayout.Button("将角色总根节点设为骨骼搜索根节点"))
        {
            foreach (UnityEngine.Object selectedTarget in targets)
            {
                if (selectedTarget is SpriteResolverSkinRebinder rebinder)
                {
                    Undo.RecordObject(rebinder, "Set Bone Search Root");
                    Transform root = rebinder.transform.root != null ? rebinder.transform.root : rebinder.transform;
                    SetPrivateField(rebinder, "boneSearchRoot", root);
                    EditorUtility.SetDirty(rebinder);
                    rebinder.RebindNow(true);
                }
            }
        }
    }

    [MenuItem("工具/2D Animation/打开精灵交换工具")]
    private static void OpenSpriteSwapTool() => EditorWindow.GetWindow<SpriteSwapToolWindow>("精灵交换工具");

    [MenuItem("CONTEXT/SpriteLibrary/为子对象配置换装骨骼自动重绑")]
    private static void SetupFromSpriteLibrary(MenuCommand command)
    {
        if (command.context is not SpriteLibrary spriteLibrary)
        {
            return;
        }

        Transform root = spriteLibrary.transform;
        int configured = ConfigureUnderRoot(root, root);
        EditorUtility.DisplayDialog("换装骨骼自动重绑", $"已在以下节点下配置 {configured} 个可换装骨骼部件：\n{GetPath(root)}", "确定");
    }

    internal static int ConfigureUnderRoot(Transform searchRoot, Transform boneSearchRoot, Action<string> logger = null)
    {
        if (searchRoot == null || boneSearchRoot == null)
        {
            return 0;
        }

        SpriteSkin[] skins = searchRoot.GetComponentsInChildren<SpriteSkin>(true);
        int configured = 0;
        foreach (SpriteSkin skin in skins)
        {
            if (skin == null)
            {
                continue;
            }

            GameObject targetObject = skin.gameObject;
            SpriteResolver resolver = targetObject.GetComponent<SpriteResolver>();
            SpriteRenderer renderer = targetObject.GetComponent<SpriteRenderer>();
            if (renderer == null)
            {
                continue;
            }

            // SpriteSkin children should also have a Sprite Resolver so they can switch labels.
            if (resolver == null)
            {
                resolver = Undo.AddComponent<SpriteResolver>(targetObject);
            }

            Undo.RecordObject(targetObject, "Setup Sprite Resolver Skin Rebinder");
            SpriteResolverSkinRebinder rebinder = targetObject.GetComponent<SpriteResolverSkinRebinder>();
            if (rebinder == null)
            {
                rebinder = Undo.AddComponent<SpriteResolverSkinRebinder>(targetObject);
            }
            else
            {
                Undo.RecordObject(rebinder, "Setup Sprite Resolver Skin Rebinder");
            }

            SetPrivateField(rebinder, "spriteSkin", skin);
            SetPrivateField(rebinder, "spriteResolver", resolver);
            SetPrivateField(rebinder, "spriteRenderer", renderer);
            SetPrivateField(rebinder, "boneSearchRoot", boneSearchRoot);
            SetPrivateField(rebinder, "fallbackToObjectRootWhenIncomplete", true);
            SetPrivateField(rebinder, "autoRebind", true);
            SetPrivateField(rebinder, "autoRebindInEditMode", true);
            SetPrivateField(rebinder, "watchSpriteChanges", true);
            SetPrivateField(rebinder, "keepSpriteSkinRootAsSearchRoot", true);

            rebinder.RebindNow();
            EditorUtility.SetDirty(rebinder);
            EditorUtility.SetDirty(skin);
            logger?.Invoke($"配置重绑：{GetPath(targetObject.transform)}");
            configured++;
        }

        if (configured > 0)
        {
            EditorUtility.SetDirty(searchRoot.gameObject);
        }

        return configured;
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        if (target == null)
        {
            return;
        }

        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field != null)
        {
            field.SetValue(target, value);
        }
    }

    private static string GetPath(Transform transform)
    {
        if (transform == null)
        {
            return "<null>";
        }

        string path = transform.name;
        while (transform.parent != null)
        {
            transform = transform.parent;
            path = transform.name + "/" + path;
        }

        return path;
    }
}

public sealed class SpriteSwapToolWindow : EditorWindow
{
    [Serializable]
    private sealed class TargetBinding
    {
        public GameObject target;
        public SpriteLibraryAsset spriteLibraryAsset;
    }

    private sealed class OperationLogEntry
    {
        public DateTime time;
        public string title;
        public string detail;
        public readonly List<string> affectedObjects = new List<string>();
        public readonly List<string> assets = new List<string>();
        public bool expanded;

        public string Header => $"[{time:HH:mm:ss}] {title}";
    }

    private readonly List<TargetBinding> bindings = new List<TargetBinding>();
    private readonly List<OperationLogEntry> operationLogs = new List<OperationLogEntry>();
    private Vector2 mainScroll;
    private Vector2 bindingScroll;
    private Vector2 logScroll;
    private float logPanelHeight = 240f;
    private bool isResizingLogPanel;
    private float resizeStartMouseY;
    private float resizeStartHeight;

    private void OnEnable()
    {
        minSize = new Vector2(760f, 520f);
    }

    private void OnGUI()
    {
        mainScroll = EditorGUILayout.BeginScrollView(mainScroll);
        EditorGUILayout.LabelField("2D 换装骨骼自动重绑工具", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "推荐流程：1. 批量选对象；2. 先在 Project 里手动创建并整理好 Sprite Library Asset，做完标签后拖到对应对象行；3. 给所有对象添加 Sprite Library 组件；4. 资产都配好后才解锁，批量添加 Sprite Resolver 和重绑脚本；5. 可选清理同级多余精灵。",
            MessageType.Info);

        DrawTargetSection();
        EditorGUILayout.Space(6f);
        DrawBindingSection();
        EditorGUILayout.Space(6f);
        DrawBatchActionSection();
        EditorGUILayout.Space(6f);
        DrawLogSection();
        EditorGUILayout.EndScrollView();
    }

    private void DrawTargetSection()
    {
        EditorGUILayout.LabelField("步骤 1：批量选择要处理的对象", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("每一行都对应一个处理对象。后面的资产字段和执行按钮都按这一行一一对应。", MessageType.None);

        using (new EditorGUILayout.VerticalScope("box"))
        {
            if (bindings.Count == 0)
            {
                EditorGUILayout.HelpBox("当前还没有对象。点击“添加对象”或直接拖入场景中的角色根节点。", MessageType.Warning);
            }

            int removeIndex = -1;
            for (int i = 0; i < bindings.Count; i++)
            {
                TargetBinding binding = bindings[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    GameObject previousTarget = binding.target;
                    binding.target = (GameObject)EditorGUILayout.ObjectField($"对象 {i + 1}", binding.target, typeof(GameObject), true);
                    if (binding.target != null && binding.target != previousTarget)
                    {
                        EditorGUIUtility.PingObject(binding.target);
                        AddLog(
                            "选择处理对象",
                            $"第 {i + 1} 行已绑定到场景对象或预制体对象：{GetPath(binding.target.transform)}。",
                            new[] { GetPath(binding.target.transform) });
                    }

                    if (GUILayout.Button("移除", GUILayout.Width(48)))
                    {
                        removeIndex = i;
                    }
                }
            }

            if (removeIndex >= 0)
            {
                string removedName = bindings[removeIndex].target != null ? GetPath(bindings[removeIndex].target.transform) : $"对象 {removeIndex + 1}";
                bindings.RemoveAt(removeIndex);
                AddLog("移除处理对象", $"已从批量处理列表移除：{removedName}。", new[] { removedName });
            }

            if (GUILayout.Button("添加对象"))
            {
                bindings.Add(new TargetBinding());
                AddLog("添加空对象槽位", "已新增一行空槽位，请把要处理的角色根节点拖进来。");
            }
        }
    }

    private void DrawBindingSection()
    {
        EditorGUILayout.LabelField("步骤 2：给所有对象添加精灵资产库组件，并绑定 Sprite Library Asset", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "先自己在 Project 里创建并整理好 Sprite Library Asset，做好类别/标签后，再拖到对应对象这一行。右侧的小圆点也可以直接从项目里选已有资产。全部绑定完成后，步骤 4 才会解锁。",
            MessageType.None);

        using (new EditorGUILayout.VerticalScope("box"))
        {
            if (bindings.Count == 0)
            {
                EditorGUILayout.HelpBox("先在步骤 1 添加对象，再给每个对象对应分配一个 Sprite Library Asset。", MessageType.Info);
            }

            bindingScroll = EditorGUILayout.BeginScrollView(bindingScroll, GUILayout.MinHeight(120f));
            for (int i = 0; i < bindings.Count; i++)
            {
                TargetBinding binding = bindings[i];
                using (new EditorGUILayout.HorizontalScope("box"))
                {
                    EditorGUILayout.ObjectField(binding.target, typeof(GameObject), true, GUILayout.Width(220f));
                    SpriteLibraryAsset previousAsset = binding.spriteLibraryAsset;
                    binding.spriteLibraryAsset = (SpriteLibraryAsset)EditorGUILayout.ObjectField(binding.spriteLibraryAsset, typeof(SpriteLibraryAsset), false);
                    if (binding.spriteLibraryAsset != null && binding.spriteLibraryAsset != previousAsset)
                    {
                        EditorGUIUtility.PingObject(binding.spriteLibraryAsset);
                        string targetName = binding.target != null ? GetPath(binding.target.transform) : $"第 {i + 1} 行未绑定对象";
                        AddLog(
                            "选择 Sprite Library Asset",
                            $"{targetName} 已选择资产：{binding.spriteLibraryAsset.name}。此时还只是窗口记录，点击“应用”或“将已配置资产应用到全部组件”后才会写入 Sprite Library 组件。",
                            binding.target != null ? new[] { targetName } : null,
                            new[] { binding.spriteLibraryAsset.name });
                    }

                    bool canApply = binding.target != null && binding.spriteLibraryAsset != null;
                    using (new EditorGUI.DisabledScope(!canApply))
                    {
                        if (GUILayout.Button("应用", GUILayout.Width(56f)))
                        {
                            ApplyBindingAsset(binding);
                        }
                    }
                }
            }
            EditorGUILayout.EndScrollView();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("给所有对象添加精灵资产库组件"))
                {
                    int added = AddSpriteLibraryComponents();
                    EditorUtility.DisplayDialog("已完成", $"已为 {added} 个对象添加精灵资产库组件。", "确定");
                }

                if (GUILayout.Button("将已配置资产应用到全部组件"))
                {
                    int applied = ApplyAllBindingAssets();
                    EditorUtility.DisplayDialog("已完成", $"已将已配置资产应用到 {applied} 个对象。", "确定");
                }
            }
        }
    }

    private void DrawBatchActionSection()
    {
        bool hasTargets = bindings.Any(binding => binding.target != null);
        bool allAssetsConfigured = bindings.Any(binding => binding.target != null) &&
                                   bindings.Where(binding => binding.target != null).All(binding => binding.spriteLibraryAsset != null);

        using (new EditorGUI.DisabledScope(!hasTargets))
        {
            using (new EditorGUI.DisabledScope(!allAssetsConfigured))
            {
                if (GUILayout.Button("步骤 4：批量自动添加精灵协调器（Sprite Resolver）和骨骼重绑脚本"))
                {
                    ApplyAllBindingAssets();
                    int configured = 0;
                    List<string> processedTargets = new List<string>();
                    foreach (TargetBinding binding in bindings.Where(item => item.target != null))
                    {
                        Transform boneRoot = binding.target.transform.root != null ? binding.target.transform.root : binding.target.transform;
                        configured += SpriteResolverSkinRebinderEditor.ConfigureUnderRoot(
                            binding.target.transform,
                            boneRoot,
                            message => AddLog(
                                "配置骨骼重绑部件",
                                "已在该 SpriteSkin 子对象上确认/添加 Sprite Resolver 与 SpriteResolverSkinRebinder，并按当前 Sprite 执行一次骨骼重绑。",
                                new[] { message.Replace("配置重绑：", string.Empty) }));
                        processedTargets.Add(GetPath(binding.target.transform));
                    }

                    AddLog(
                        "步骤 4 批量配置完成",
                        $"已给子层级中的 SpriteSkin 对象补齐 Sprite Resolver 与 SpriteResolverSkinRebinder，并按当前精灵尝试重绑骨骼。配置数量：{configured}。",
                        processedTargets);
                    EditorUtility.DisplayDialog("步骤 4 完成", $"已配置 {configured} 个骨骼重绑部件。", "确定");
                }
            }

            if (!allAssetsConfigured)
            {
                EditorGUILayout.HelpBox("步骤 4 尚未解锁：请先把每个对象对应的 Sprite Library Asset 都拖好并应用。", MessageType.Warning);
            }

            if (GUILayout.Button("步骤 5（可选）：批量删除同级多余精灵"))
            {
                if (EditorUtility.DisplayDialog("确认清理", "每个父节点仅保留第一个 SpriteRenderer 子对象，操作可撤销。继续吗？", "继续", "取消"))
                {
                    foreach (TargetBinding binding in bindings)
                    {
                        if (binding.target != null)
                        {
                            int removed = RemoveExtraSprites(binding.target);
                            AddLog(
                                "步骤 5 清理同级多余精灵",
                                $"每个父节点仅保留第一个 SpriteRenderer 子对象。本次删除数量：{removed}。",
                                new[] { GetPath(binding.target.transform) });
                        }
                    }
                }
            }
        }
    }

    private void DrawLogSection()
    {
        EditorGUILayout.LabelField("操作日志", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("记录你做过的对象、动作和时间。点击每条左侧小三角可以展开详情，底部边框可上下拖动。", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(operationLogs.Count == 0))
                {
                    if (GUILayout.Button("全部展开", GUILayout.Width(72f)))
                    {
                        SetAllLogsExpanded(true);
                    }

                    if (GUILayout.Button("全部收起", GUILayout.Width(72f)))
                    {
                        SetAllLogsExpanded(false);
                    }
                }

                if (GUILayout.Button("清空日志", GUILayout.Width(72f)))
                {
                    operationLogs.Clear();
                }
            }

            logScroll = EditorGUILayout.BeginScrollView(logScroll, GUILayout.MinHeight(logPanelHeight), GUILayout.MaxHeight(logPanelHeight));
            if (operationLogs.Count == 0)
            {
                EditorGUILayout.HelpBox("还没有日志。执行添加对象、绑定资产、应用、重绑或清理后，这里会显示记录。", MessageType.None);
            }
            else
            {
                foreach (OperationLogEntry log in operationLogs)
                {
                    DrawLogEntry(log);
                }
            }
            EditorGUILayout.EndScrollView();
            DrawLogResizeHandle();
        }
    }

    private void DrawLogEntry(OperationLogEntry log)
    {
        using (new EditorGUILayout.VerticalScope("box"))
        {
            log.expanded = EditorGUILayout.Foldout(log.expanded, log.Header, true);
            if (!log.expanded)
            {
                return;
            }

            EditorGUI.indentLevel++;
            if (!string.IsNullOrWhiteSpace(log.detail))
            {
                EditorGUILayout.LabelField("详情", EditorStyles.miniBoldLabel);
                EditorGUILayout.SelectableLabel(log.detail, EditorStyles.wordWrappedLabel, GUILayout.MinHeight(EditorGUIUtility.singleLineHeight * 2f));
            }

            if (log.affectedObjects.Count > 0)
            {
                EditorGUILayout.LabelField($"影响对象（{log.affectedObjects.Count}）", EditorStyles.miniBoldLabel);
                for (int i = 0; i < log.affectedObjects.Count; i++)
                {
                    EditorGUILayout.SelectableLabel($"{i + 1}. {log.affectedObjects[i]}", EditorStyles.wordWrappedMiniLabel, GUILayout.MinHeight(EditorGUIUtility.singleLineHeight + 2f));
                }
            }

            if (log.assets.Count > 0)
            {
                EditorGUILayout.LabelField($"相关资产（{log.assets.Count}）", EditorStyles.miniBoldLabel);
                for (int i = 0; i < log.assets.Count; i++)
                {
                    EditorGUILayout.SelectableLabel($"{i + 1}. {log.assets[i]}", EditorStyles.wordWrappedMiniLabel, GUILayout.MinHeight(EditorGUIUtility.singleLineHeight + 2f));
                }
            }

            EditorGUI.indentLevel--;
        }
    }

    private void DrawLogResizeHandle()
    {
        Rect handleRect = GUILayoutUtility.GetRect(0f, 8f, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(new Rect(handleRect.x + 6f, handleRect.y + 3f, handleRect.width - 12f, 1f), new Color(0.45f, 0.45f, 0.45f, 0.85f));
        EditorGUI.DrawRect(new Rect(handleRect.x + 6f, handleRect.y + 5f, handleRect.width - 12f, 1f), new Color(0.28f, 0.28f, 0.28f, 0.85f));
        EditorGUIUtility.AddCursorRect(handleRect, MouseCursor.ResizeVertical);

        Event current = Event.current;
        int controlId = GUIUtility.GetControlID(FocusType.Passive);
        switch (current.GetTypeForControl(controlId))
        {
            case EventType.MouseDown:
                if (current.button == 0 && handleRect.Contains(current.mousePosition))
                {
                    GUIUtility.hotControl = controlId;
                    isResizingLogPanel = true;
                    resizeStartMouseY = current.mousePosition.y;
                    resizeStartHeight = logPanelHeight;
                    current.Use();
                }
                break;

            case EventType.MouseDrag:
                if (isResizingLogPanel && GUIUtility.hotControl == controlId)
                {
                    float delta = current.mousePosition.y - resizeStartMouseY;
                    logPanelHeight = Mathf.Clamp(resizeStartHeight + delta, 160f, 520f);
                    Repaint();
                    current.Use();
                }
                break;

            case EventType.MouseUp:
                if (isResizingLogPanel && GUIUtility.hotControl == controlId)
                {
                    isResizingLogPanel = false;
                    GUIUtility.hotControl = 0;
                    current.Use();
                }
                break;
        }
    }

    private int AddSpriteLibraryComponents()
    {
        int added = 0;
        List<string> affectedObjects = new List<string>();
        List<string> existingObjects = new List<string>();
        foreach (TargetBinding binding in bindings)
        {
            if (binding.target == null)
            {
                continue;
            }

            if (binding.target.GetComponent<SpriteLibrary>() == null)
            {
                Undo.AddComponent<SpriteLibrary>(binding.target);
                affectedObjects.Add(GetPath(binding.target.transform));
                added++;
            }
            else
            {
                existingObjects.Add(GetPath(binding.target.transform));
            }
        }

        AddLog(
            "步骤 3 添加 Sprite Library 组件",
            $"新增组件：{added} 个；已存在组件并跳过：{existingObjects.Count} 个。",
            affectedObjects.Concat(existingObjects));
        return added;
    }

    private int ApplyAllBindingAssets()
    {
        int applied = 0;
        foreach (TargetBinding binding in bindings)
        {
            if (ApplyBindingAsset(binding))
            {
                applied++;
            }
        }

        return applied;
    }

    private bool ApplyBindingAsset(TargetBinding binding)
    {
        if (binding == null || binding.target == null || binding.spriteLibraryAsset == null)
        {
            return false;
        }

        SpriteLibrary library = binding.target.GetComponent<SpriteLibrary>();
        if (library == null)
        {
            library = Undo.AddComponent<SpriteLibrary>(binding.target);
            AddLog(
                "自动补加 Sprite Library 组件",
                "应用资产时发现目标对象没有 Sprite Library 组件，已自动补加后继续写入资产。",
                new[] { GetPath(binding.target.transform) });
        }

        Undo.RecordObject(library, "Apply Sprite Library Asset");
        SerializedObject serializedLibrary = new SerializedObject(library);
        SerializedProperty assetProperty = serializedLibrary.FindProperty("m_SpriteLibraryAsset");
        if (assetProperty != null)
        {
            assetProperty.objectReferenceValue = binding.spriteLibraryAsset;
            serializedLibrary.ApplyModifiedProperties();
            EditorUtility.SetDirty(library);
            AddLog(
                "应用 Sprite Library Asset",
                $"已把资产写入目标对象的 Sprite Library 组件：{binding.spriteLibraryAsset.name}。",
                new[] { GetPath(binding.target.transform) },
                new[] { AssetDatabase.GetAssetPath(binding.spriteLibraryAsset) });
            return true;
        }

        return false;
    }

    private int RemoveExtraSprites(GameObject selected)
    {
        int removed = 0;
        List<string> removedObjects = new List<string>();
        Transform[] parents = selected.GetComponentsInChildren<Transform>(true);
        foreach (Transform parent in parents.Where(p => p != null))
        {
            Transform[] children = new Transform[parent.childCount];
            for (int c = 0; c < parent.childCount; c++)
            {
                children[c] = parent.GetChild(c);
            }

            List<Transform> sprites = children.Where(c => c != null && c.GetComponent<SpriteRenderer>() != null).ToList();
            for (int i = 1; i < sprites.Count; i++)
            {
                removedObjects.Add(GetPath(sprites[i]));
                Undo.DestroyObjectImmediate(sprites[i].gameObject);
                removed++;
            }
        }

        if (removedObjects.Count > 0)
        {
            AddLog(
                "删除多余 SpriteRenderer 子对象明细",
                "以下对象已被删除。该操作可通过 Unity Undo 撤回。",
                removedObjects);
        }

        EditorUtility.DisplayDialog("清理完成", $"已删除 {removed} 个精灵对象。", "确定");
        return removed;
    }

    private void AddLog(string message)
    {
        AddLog(message, null, null, null);
    }

    private void AddLog(string title, string detail, IEnumerable<string> affectedObjects = null, IEnumerable<string> assets = null)
    {
        OperationLogEntry entry = new OperationLogEntry
        {
            time = DateTime.Now,
            title = string.IsNullOrWhiteSpace(title) ? "未命名操作" : title,
            detail = detail ?? string.Empty,
            expanded = false
        };

        if (affectedObjects != null)
        {
            entry.affectedObjects.AddRange(affectedObjects.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct());
        }

        if (assets != null)
        {
            entry.assets.AddRange(assets.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct());
        }

        operationLogs.Add(entry);
        if (operationLogs.Count > 200)
        {
            operationLogs.RemoveAt(0);
        }
        Repaint();
    }

    private void SetAllLogsExpanded(bool expanded)
    {
        foreach (OperationLogEntry log in operationLogs)
        {
            log.expanded = expanded;
        }
    }

    private static string GetPath(Transform transform)
    {
        if (transform == null)
        {
            return "<null>";
        }

        string path = transform.name;
        while (transform.parent != null)
        {
            transform = transform.parent;
            path = transform.name + "/" + path;
        }

        return path;
    }
}
