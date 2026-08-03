using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.U2D;
using UnityEngine.U2D.Animation;

/// <summary>
/// Rebinds a SpriteSkin after a SpriteResolver swaps to a Sprite that uses a different bone set.
/// This is a pragmatic bridge for wardrobe prototypes. Prefer shared/superset bones or separate
/// equipment prefabs for production rigs with very different skeletons.
/// </summary>
[ExecuteAlways]
[DefaultExecutionOrder(10000)]
[DisallowMultipleComponent]
[RequireComponent(typeof(SpriteRenderer))]
public sealed class SpriteResolverSkinRebinder : MonoBehaviour
{
    public enum MissingBonePolicy
    {
        KeepCurrentBinding,
        DisableSpriteSkin,
        UseObjectTransform,
    }

    [Header("References")]
    [Tooltip("需要重绑的 SpriteSkin。为空时自动取当前对象上的 SpriteSkin。")]
    [SerializeField] private SpriteSkin spriteSkin;
    [Tooltip("换装使用的 SpriteResolver。为空时自动取当前对象上的 SpriteResolver。")]
    [SerializeField] private SpriteResolver spriteResolver;
    [Tooltip("SpriteRenderer。为空时自动取当前对象上的 SpriteRenderer。")]
    [SerializeField] private SpriteRenderer spriteRenderer;
    [Tooltip("查找骨骼的稳定根节点。建议拖角色总根节点/骨架总父节点，必须包含所有可切换外观会用到的骨骼；不要拖会随 SpriteSkin 切换而变化的 rootBone。为空时使用当前对象的场景根节点。")]
    [SerializeField] private Transform boneSearchRoot;
    [Tooltip("如果当前 Bone Search Root 找不齐骨骼，则自动退回当前对象根节点再找一次，避免旧组件里缓存了某个子 bone 导致只同步前几根骨骼。")]
    [SerializeField] private bool fallbackToObjectRootWhenIncomplete = true;

    [Header("Rebind")]
    [Tooltip("启用后在 OnEnable/Start/检测到 Sprite 变化时自动按当前 Sprite 的骨骼重新绑定。")]
    [SerializeField] private bool autoRebind = true;
    [Tooltip("启用后编辑器非运行状态也会自动检测 SpriteResolver/SpriteRenderer 的 Sprite 变化并刷新 SpriteSkin Bones。")]
    [SerializeField] private bool autoRebindInEditMode = true;
    [Tooltip("启用后每帧检测 Sprite 是否发生变化。SpriteResolver 切换没有稳定的公开事件，所以默认开启。")]
    [SerializeField] private bool watchSpriteChanges = true;
    [Tooltip("启用后 SpriteSkin.Root Bone 会保持为 Bone Search Root。建议开启，这样不会因为切换到某张图后 Root Bone 缩到局部 bone，导致后续骨骼查找不完整。")]
    [SerializeField] private bool keepSpriteSkinRootAsSearchRoot = true;
    [Tooltip("找不到某根骨骼时的处理。建议开发期 KeepCurrentBinding，便于发现缺骨骼。")]
    [SerializeField] private MissingBonePolicy missingBonePolicy = MissingBonePolicy.KeepCurrentBinding;
    [Tooltip("按名字匹配失败时，再用完整路径匹配。")]
    [SerializeField] private bool matchByPathWhenNameDuplicated = true;
    [Tooltip("大小写不敏感匹配骨骼名。")]
    [SerializeField] private bool ignoreCase = false;
    [Tooltip("重绑失败时输出警告。")]
    [SerializeField] private bool logWarnings = true;

    private static readonly BindingFlags InstanceAny = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly PropertyInfo RootBoneProperty = typeof(SpriteSkin).GetProperty("rootBone", InstanceAny);
    private static readonly PropertyInfo BoneTransformsProperty = typeof(SpriteSkin).GetProperty("boneTransforms", InstanceAny);
    private static readonly MethodInfo CacheCurrentSpriteMethod = typeof(SpriteSkin).GetMethod("CacheCurrentSprite", InstanceAny);
    private static readonly MethodInfo CacheHierarchyMethod = typeof(SpriteSkin).GetMethod("CacheHierarchy", InstanceAny);

    private Sprite lastSprite;
#if UNITY_EDITOR
    private bool editorRebindQueued;
    private int pendingEditorRebindPasses;
#endif
    private readonly Dictionary<string, List<Transform>> bonesByName = new Dictionary<string, List<Transform>>();
    private readonly Dictionary<string, Transform> bonesByGuid = new Dictionary<string, Transform>();

    private void Reset()
    {
        AutoBindReferences();
        if (boneSearchRoot == null)
        {
            boneSearchRoot = ResolveDefaultBoneSearchRoot();
        }
    }

    private void Awake()
    {
        AutoBindReferences();
    }

    private void OnEnable()
    {
        AutoBindReferences();
        if (autoRebind)
        {
            RebindSafely();
        }
    }

    private void Start()
    {
        if (autoRebind)
        {
            RebindSafely();
        }
    }

    private void LateUpdate()
    {
        if (!ShouldWatchForSpriteChanges())
        {
            return;
        }

        Sprite currentSprite = spriteRenderer.sprite;
        if (currentSprite == lastSprite)
        {
            return;
        }

        RebindSafely();
    }

    private void OnValidate()
    {
        AutoBindReferences();
#if UNITY_EDITOR
        QueueEditorRebind();
#endif
    }

    [ContextMenu("Rebind SpriteSkin Now")]
    public bool RebindNow(bool recordUndo = false)
    {
        AutoBindReferences();
        if (spriteSkin == null || spriteRenderer == null)
        {
            return false;
        }

        Sprite sprite = spriteRenderer.sprite;
        lastSprite = sprite;
        if (sprite == null)
        {
            return false;
        }

        SpriteBone[] spriteBones = sprite.GetBones();
        if (spriteBones == null || spriteBones.Length == 0)
        {
            spriteSkin.enabled = false;
            return false;
        }

        Transform searchRoot = boneSearchRoot != null ? boneSearchRoot : ResolveDefaultBoneSearchRoot();
        if (searchRoot == null)
        {
            Warn($"Cannot rebind {name}: bone search root is missing.");
            return false;
        }

        Transform[] resolvedTransforms = ResolveAllBones(spriteBones, searchRoot, out List<string> missingBones);
        if (missingBones.Count > 0 && fallbackToObjectRootWhenIncomplete)
        {
            Transform fallbackRoot = ResolveDefaultBoneSearchRoot();
            if (fallbackRoot != null && fallbackRoot != searchRoot)
            {
                Transform[] fallbackResolvedTransforms = ResolveAllBones(spriteBones, fallbackRoot, out List<string> fallbackMissingBones);
                if (fallbackMissingBones.Count < missingBones.Count)
                {
                    searchRoot = fallbackRoot;
                    resolvedTransforms = fallbackResolvedTransforms;
                    missingBones = fallbackMissingBones;
                }
            }
        }

        if (missingBones.Count > 0)
        {
            Warn($"Cannot fully rebind {name} for Sprite '{sprite.name}'. Missing bones: {string.Join(", ", missingBones)}.");
            if (missingBonePolicy == MissingBonePolicy.KeepCurrentBinding)
            {
                return false;
            }

            if (missingBonePolicy == MissingBonePolicy.DisableSpriteSkin)
            {
                spriteSkin.enabled = false;
                return false;
            }

            for (int i = 0; i < resolvedTransforms.Length; i++)
            {
                if (resolvedTransforms[i] == null)
                {
                    resolvedTransforms[i] = transform;
                }
            }
        }

        Transform rootBone = keepSpriteSkinRootAsSearchRoot ? searchRoot : ResolveRootBone(spriteBones, resolvedTransforms, searchRoot);
        bool applied = ApplySpriteSkinBinding(rootBone, resolvedTransforms, recordUndo);
        if (applied)
        {
            spriteSkin.enabled = true;
        }

        return applied;
    }

    private void AutoBindReferences()
    {
        if (spriteRenderer == null)
        {
            spriteRenderer = GetComponent<SpriteRenderer>();
        }

        if (spriteSkin == null)
        {
            spriteSkin = GetComponent<SpriteSkin>();
        }

        if (spriteResolver == null)
        {
            spriteResolver = GetComponent<SpriteResolver>();
        }
    }

    private bool ShouldWatchForSpriteChanges()
    {
        if (!autoRebind || !watchSpriteChanges || spriteRenderer == null)
        {
            return false;
        }

        if (Application.isPlaying)
        {
            return true;
        }

        return autoRebindInEditMode;
    }

    private bool RebindSafely()
    {
        if (!Application.isPlaying && !autoRebindInEditMode)
        {
            return false;
        }

        return RebindNow();
    }

    private Transform ResolveDefaultBoneSearchRoot()
    {
        return transform.root != null ? transform.root : transform;
    }

    private Transform[] ResolveAllBones(SpriteBone[] spriteBones, Transform searchRoot, out List<string> missingBones)
    {
        CacheBones(searchRoot);
        Transform[] resolvedTransforms = new Transform[spriteBones.Length];
        missingBones = new List<string>();
        for (int i = 0; i < spriteBones.Length; i++)
        {
            Transform bone = ResolveBone(spriteBones, i);
            resolvedTransforms[i] = bone;
        }

        for (int i = 0; i < spriteBones.Length; i++)
        {
            if (resolvedTransforms[i] != null)
            {
                continue;
            }

            Transform fallbackBone = ResolveMissingBoneFallback(spriteBones, resolvedTransforms, i, searchRoot);
            resolvedTransforms[i] = fallbackBone;
            if (fallbackBone == null)
            {
                missingBones.Add(spriteBones[i].name);
            }
        }

        return resolvedTransforms;
    }

    private Transform ResolveMissingBoneFallback(SpriteBone[] spriteBones, Transform[] resolvedTransforms, int index, Transform searchRoot)
    {
        int parentIndex = index >= 0 && index < spriteBones.Length ? spriteBones[index].parentId : -1;
        if (parentIndex >= 0 && parentIndex < resolvedTransforms.Length && resolvedTransforms[parentIndex] != null)
        {
            return resolvedTransforms[parentIndex];
        }

        if (searchRoot != null)
        {
            return searchRoot;
        }

        return transform;
    }

    private void CacheBones(Transform root)
    {
        bonesByName.Clear();
        bonesByGuid.Clear();
        StringComparer comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            string nameKey = comparer == StringComparer.OrdinalIgnoreCase ? child.name.ToLowerInvariant() : child.name;
            if (!bonesByName.TryGetValue(nameKey, out List<Transform> list))
            {
                list = new List<Transform>();
                bonesByName[nameKey] = list;
            }

            list.Add(child);
            string guid = ReadUnity2DBoneGuid(child);
            if (!string.IsNullOrEmpty(guid))
            {
                string guidKey = comparer == StringComparer.OrdinalIgnoreCase ? guid.ToLowerInvariant() : guid;
                bonesByGuid[guidKey] = child;
            }
        }
    }

    private Transform ResolveBone(SpriteBone[] spriteBones, int index)
    {
        string boneGuid = spriteBones[index].guid;
        if (!string.IsNullOrEmpty(boneGuid))
        {
            string guidKey = ignoreCase ? boneGuid.ToLowerInvariant() : boneGuid;
            if (bonesByGuid.TryGetValue(guidKey, out Transform guidMatch) && guidMatch != null)
            {
                return guidMatch;
            }
        }

        string boneName = spriteBones[index].name;
        string nameKey = ignoreCase ? boneName.ToLowerInvariant() : boneName;
        if (!bonesByName.TryGetValue(nameKey, out List<Transform> matches) || matches == null || matches.Count == 0)
        {
            return null;
        }

        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (!matchByPathWhenNameDuplicated)
        {
            return null;
        }

        return ResolveUniquePathMatch(spriteBones, index, matches);
    }

    private Transform ResolveRootBone(SpriteBone[] spriteBones, Transform[] resolvedTransforms, Transform fallbackRoot)
    {
        for (int i = 0; i < spriteBones.Length; i++)
        {
            if (spriteBones[i].parentId < 0 && resolvedTransforms[i] != null)
            {
                return resolvedTransforms[i];
            }
        }

        return fallbackRoot;
    }

    private bool ApplySpriteSkinBinding(Transform rootBone, Transform[] boneTransforms, bool recordUndo)
    {
        if (spriteSkin == null || rootBone == null || boneTransforms == null)
        {
            return false;
        }

        if (RootBoneProperty == null || BoneTransformsProperty == null)
        {
            Warn("SpriteSkin internal binding properties are unavailable in this Unity package version.");
            return false;
        }

        try
        {
#if UNITY_EDITOR
            if (!Application.isPlaying && recordUndo)
            {
                UnityEditor.Undo.RecordObject(spriteSkin, "重绑 SpriteSkin 骨骼");
            }
#endif
            RootBoneProperty.SetValue(spriteSkin, rootBone, null);
            BoneTransformsProperty.SetValue(spriteSkin, boneTransforms, null);
            CacheHierarchyMethod?.Invoke(spriteSkin, null);
            CacheCurrentSpriteMethod?.Invoke(spriteSkin, new object[] { false });
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                ApplySpriteSkinSerializedBinding(rootBone, boneTransforms, recordUndo);
                UnityEditor.EditorUtility.SetDirty(spriteSkin);
                UnityEditor.EditorUtility.SetDirty(this);
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            }
#endif
            return true;
        }
        catch (Exception exception)
        {
            Warn($"Failed to apply SpriteSkin binding on {name}: {exception.GetType().Name} {exception.Message}");
            return false;
        }
    }

    private Transform ResolveUniquePathMatch(SpriteBone[] spriteBones, int index, List<Transform> matches)
    {
        List<string> expectedNames = BuildSpriteBoneAncestorNames(spriteBones, index);
        Transform bestMatch = null;
        int bestScore = 0;
        bool isAmbiguous = false;
        foreach (Transform candidate in matches)
        {
            int score = CountMatchingAncestorNames(expectedNames, candidate);
            if (score > bestScore)
            {
                bestScore = score;
                bestMatch = candidate;
                isAmbiguous = false;
            }
            else if (score == bestScore)
            {
                isAmbiguous = true;
            }
        }

        // A duplicate name without a unique ancestor-chain match is unsafe to bind.
        return !isAmbiguous && bestScore > 0 ? bestMatch : null;
    }

    private List<string> BuildSpriteBoneAncestorNames(SpriteBone[] spriteBones, int index)
    {
        List<string> names = new List<string>();
        int current = index;
        int safety = spriteBones.Length + 1;
        while (current >= 0 && current < spriteBones.Length && safety-- > 0)
        {
            names.Add(NormalizeKey(spriteBones[current].name));
            current = spriteBones[current].parentId;
        }

        return names;
    }

    private int CountMatchingAncestorNames(List<string> expectedNames, Transform candidate)
    {
        int score = 0;
        Transform current = candidate;
        for (int i = 0; i < expectedNames.Count && current != null; i++)
        {
            if (NormalizeKey(current.name) != expectedNames[i])
            {
                break;
            }

            score++;
            current = current.parent;
        }

        return score;
    }

    private string NormalizeKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        value = value.Trim();
        value = value.Replace(" 拷贝", string.Empty, StringComparison.OrdinalIgnoreCase);
        value = value.Replace(" 副本", string.Empty, StringComparison.OrdinalIgnoreCase);
        value = value.Replace(" copy", string.Empty, StringComparison.OrdinalIgnoreCase);
        value = value.Replace("(copy)", string.Empty, StringComparison.OrdinalIgnoreCase);
        value = value.Replace("（copy）", string.Empty, StringComparison.OrdinalIgnoreCase);
        return ignoreCase ? value.ToLowerInvariant() : value;
    }

    private static string ReadUnity2DBoneGuid(Transform boneTransform)
    {
        if (boneTransform == null)
        {
            return null;
        }

        Component[] components = boneTransform.GetComponents<Component>();
        for (int i = 0; i < components.Length; i++)
        {
            Component component = components[i];
            if (component == null)
            {
                continue;
            }

            Type type = component.GetType();
            if (type.FullName != "UnityEngine.U2D.Animation.Bone")
            {
                continue;
            }

            PropertyInfo guidProperty = type.GetProperty("guid", InstanceAny);
            return guidProperty != null ? guidProperty.GetValue(component, null) as string : null;
        }

        return null;
    }

    private void Warn(string message)
    {
        if (logWarnings)
        {
            Debug.LogWarning($"[SpriteResolverSkinRebinder] {message}", this);
        }
    }

#if UNITY_EDITOR
    private void QueueEditorRebind()
    {
        if (Application.isPlaying || !autoRebind || !autoRebindInEditMode)
        {
            return;
        }

        pendingEditorRebindPasses = Mathf.Max(pendingEditorRebindPasses, 2);
        if (!editorRebindQueued)
        {
            editorRebindQueued = true;
            UnityEditor.EditorApplication.delayCall += RunQueuedEditorRebind;
        }
    }

    private void RunQueuedEditorRebind()
    {
        editorRebindQueued = false;
        if (this == null || Application.isPlaying || !autoRebind || !autoRebindInEditMode)
        {
            pendingEditorRebindPasses = 0;
            return;
        }

        RebindNow();
        pendingEditorRebindPasses--;
        if (pendingEditorRebindPasses > 0)
        {
            editorRebindQueued = true;
            UnityEditor.EditorApplication.delayCall += RunQueuedEditorRebind;
        }
    }

    private void ApplySpriteSkinSerializedBinding(Transform rootBone, Transform[] boneTransforms, bool recordUndo)
    {
        if (spriteSkin == null || rootBone == null || boneTransforms == null)
        {
            return;
        }

        UnityEditor.SerializedObject serializedSkin = new UnityEditor.SerializedObject(spriteSkin);
        UnityEditor.SerializedProperty rootBoneProperty = serializedSkin.FindProperty("m_RootBone");
        if (rootBoneProperty != null)
        {
            rootBoneProperty.objectReferenceValue = rootBone;
        }

        UnityEditor.SerializedProperty boneTransformsProperty = serializedSkin.FindProperty("m_BoneTransforms");
        if (boneTransformsProperty != null && boneTransformsProperty.isArray)
        {
            boneTransformsProperty.arraySize = boneTransforms.Length;
            for (int i = 0; i < boneTransforms.Length; i++)
            {
                UnityEditor.SerializedProperty element = boneTransformsProperty.GetArrayElementAtIndex(i);
                if (element != null)
                {
                    element.objectReferenceValue = boneTransforms[i];
                }
            }
        }

        if (recordUndo)
        {
            serializedSkin.ApplyModifiedProperties();
        }
        else
        {
            serializedSkin.ApplyModifiedPropertiesWithoutUndo();
        }
    }
#endif
}
