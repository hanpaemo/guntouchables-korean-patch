using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.Localization.Settings;
using UnityEngine.SceneManagement;
using UnityEngine.TextCore;
using UnityEngine.TextCore.LowLevel;

namespace Hanpaemo.Guntouchables;

public static class KoreanFontRuntime
{
    private const string DriverObjectName = "__HanpaemoGuntouchablesKoreanFont";
    private const string RequiredCharsetFileName = "required_chars_ko.txt";
    private const string ProbeText =
        "\uD55C\uAE00\uD14C\uC2A4\uD2B8\uAC00\uB098\uB2E4\uB77C\uB9C8\uBC14\uC0AC\uC544\uC790\uCC28\uCE74\uD0C0\uD30C\uD5580123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const char HangulTestA = '\uD55C';
    private const char HangulTestB = '\uAE00';
    private const char HangulTestC = '\uAC00';
    private const int SamplingPointSize = 56;
    private const int AtlasPadding = 5;
    private const int AtlasSize = 4096;

    private static readonly object Sync = new();
    private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly FieldInfo VersionField = typeof(TMP_FontAsset).GetField("m_Version", InstanceFlags);
    private static readonly FieldInfo ClearDynamicDataOnBuildField = typeof(TMP_FontAsset).GetField("m_ClearDynamicDataOnBuild", InstanceFlags);
    private static readonly FieldInfo AtlasWidthField = typeof(TMP_FontAsset).GetField("m_AtlasWidth", InstanceFlags);
    private static readonly FieldInfo AtlasHeightField = typeof(TMP_FontAsset).GetField("m_AtlasHeight", InstanceFlags);
    private static readonly FieldInfo AtlasPaddingField = typeof(TMP_FontAsset).GetField("m_AtlasPadding", InstanceFlags);
    private static readonly FieldInfo AtlasRenderModeField = typeof(TMP_FontAsset).GetField("m_AtlasRenderMode", InstanceFlags);
    private static readonly FieldInfo AtlasPopulationModeField = typeof(TMP_FontAsset).GetField("m_AtlasPopulationMode", InstanceFlags);
    private static readonly FieldInfo AtlasTextureField = typeof(TMP_FontAsset).GetField("m_AtlasTexture", InstanceFlags);
    private static readonly FieldInfo AtlasTextureIndexField = typeof(TMP_FontAsset).GetField("m_AtlasTextureIndex", InstanceFlags);
    private static readonly FieldInfo AtlasTexturesField = typeof(TMP_FontAsset).GetField("m_AtlasTextures", InstanceFlags);
    private static readonly FieldInfo FreeGlyphRectsField = typeof(TMP_FontAsset).GetField("m_FreeGlyphRects", InstanceFlags);
    private static readonly FieldInfo UsedGlyphRectsField = typeof(TMP_FontAsset).GetField("m_UsedGlyphRects", InstanceFlags);
    private static readonly FieldInfo GlyphTableField = typeof(TMP_FontAsset).GetField("m_GlyphTable", InstanceFlags);
    private static readonly FieldInfo CharacterTableField = typeof(TMP_FontAsset).GetField("m_CharacterTable", InstanceFlags);
    private static readonly FieldInfo GlyphLookupDictionaryField = typeof(TMP_FontAsset).GetField("m_GlyphLookupDictionary", InstanceFlags);
    private static readonly FieldInfo CharacterLookupDictionaryField = typeof(TMP_FontAsset).GetField("m_CharacterLookupDictionary", InstanceFlags);
    private static readonly FieldInfo FontFeatureTableField = typeof(TMP_FontAsset).GetField("m_FontFeatureTable", InstanceFlags);
    private static readonly FieldInfo FontWeightTableField = typeof(TMP_FontAsset).GetField("m_FontWeightTable", InstanceFlags);
    private static readonly MethodInfo ResetAtlasTextureMethod = typeof(FontEngine).GetMethod(
        "ResetAtlasTexture",
        StaticFlags,
        null,
        new[] { typeof(Texture2D) },
        null);
    private static readonly MethodInfo TryAddGlyphsToTextureMethod = typeof(FontEngine).GetMethod(
        "TryAddGlyphsToTexture",
        StaticFlags,
        null,
        new[]
        {
            typeof(List<uint>),
            typeof(int),
            typeof(GlyphPackingMode),
            typeof(List<GlyphRect>),
            typeof(List<GlyphRect>),
            typeof(GlyphRenderMode),
            typeof(Texture2D),
            typeof(Glyph[]).MakeByRefType(),
        },
        null);

    private static TMP_FontAsset fallbackFont;
    private static KoreanFontDriver driver;
    private static string activeFontSourcePath;
    private static float nextRetryAt;
    private static int creationFailures;
    private static bool creationLogged;

    private static string ManagedDirectory =>
        Path.GetDirectoryName(typeof(KoreanFontRuntime).Assembly.Location) ?? AppDomain.CurrentDomain.BaseDirectory;

    public static void RuntimeInitialize()
    {
        try
        {
            Log("RuntimeInitialize invoked.");
            EnsureDriver();
            TryInstall("runtime-init");
        }
        catch (Exception ex)
        {
            Log("RuntimeInitialize failed: " + ex);
        }
    }

    internal static void EnsureDriver()
    {
        if (driver != null)
        {
            return;
        }

        GameObject host = GameObject.Find(DriverObjectName);
        if (host == null)
        {
            host = new GameObject(DriverObjectName);
        }

        if (host.transform.parent != null)
        {
            host.transform.SetParent(null, false);
        }

        if (host.scene.IsValid())
        {
            UnityEngine.Object.DontDestroyOnLoad(host);
        }

        driver = host.GetComponent<KoreanFontDriver>();
        if (driver == null)
        {
            driver = host.AddComponent<KoreanFontDriver>();
        }
    }

    internal static void ClearDriver(KoreanFontDriver currentDriver)
    {
        if (driver == currentDriver)
        {
            driver = null;
        }
    }

    internal static void TryInstall(string reason)
    {
        lock (Sync)
        {
            if (!EnsureFallbackFont())
            {
                return;
            }

            AttachGlobalFallback();
            PatchLoadedFontAssets();
            WarmupRequiredCharacters();
            RefreshTexts(reason);
        }
    }

    private static bool EnsureFallbackFont()
    {
        if (fallbackFont != null)
        {
            return true;
        }

        if (Time.realtimeSinceStartup < nextRetryAt)
        {
            return false;
        }

        foreach (string candidatePath in EnumerateFontCandidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!TryLoadFontFace(candidatePath, "create"))
                {
                    continue;
                }

                TMP_FontAsset created = CreateEmptyFontAsset();
                string initialSet = BuildBootstrapCharacterSet();
                bool added = TryAddCharactersToFallback(created, candidatePath, initialSet, "bootstrap");

                if (
                    !added
                    || !created.HasCharacter(HangulTestA, false, false)
                    || !created.HasCharacter(HangulTestB, false, false)
                    || !created.HasCharacter(HangulTestC, false, false)
                )
                {
                    Log("Manual TMP font bootstrap failed for [" + candidatePath + "].");
                    continue;
                }

                created.name = "Hanpaemo Korean Fallback SDF";
                fallbackFont = created;
                activeFontSourcePath = candidatePath;
                TMP_ResourceManager.AddFontAsset(created);
                Log(
                    "Created fallback TMP font from [" + candidatePath + "] with "
                        + created.characterTable.Count
                        + " characters and "
                        + created.glyphTable.Count
                        + " glyphs."
                );
                return true;
            }
            catch (Exception ex)
            {
                Log("Candidate font failed [" + candidatePath + "]: " + ex);
            }
        }

        creationFailures++;
        nextRetryAt = Time.realtimeSinceStartup + Mathf.Min(8f, creationFailures);

        if (!creationLogged || creationFailures <= 3)
        {
            creationLogged = true;
            Log("Failed to create a Korean fallback font. Next retry in " + (nextRetryAt - Time.realtimeSinceStartup) + "s.");
        }

        return false;
    }

    private static IEnumerable<string> EnumerateFontCandidates()
    {
        string localNanum = Path.Combine(ManagedDirectory, "NanumGothic.ttf");
        if (File.Exists(localNanum))
        {
            yield return localNanum;
        }

        string windowsFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (!string.IsNullOrWhiteSpace(windowsFonts))
        {
            string malgun = Path.Combine(windowsFonts, "malgun.ttf");
            string malgunBold = Path.Combine(windowsFonts, "malgunbd.ttf");

            if (File.Exists(malgun))
            {
                yield return malgun;
            }

            if (File.Exists(malgunBold))
            {
                yield return malgunBold;
            }
        }
    }

    private static bool TryLoadFontFace(string candidatePath, string reason)
    {
        try
        {
            FontEngine.InitializeFontEngine();
            FontEngineError error = FontEngine.LoadFontFace(candidatePath, SamplingPointSize, 0);
            if (error == FontEngineError.Success)
            {
                FaceInfo face = FontEngine.GetFaceInfo();
                Log(
                    "Loaded font face ["
                        + candidatePath
                        + "] for "
                        + reason
                        + ": family="
                        + (face.familyName ?? "<null>")
                        + ", style="
                        + (face.styleName ?? "<null>")
                        + ", pointSize="
                        + face.pointSize
                );
                return true;
            }

            Log("FontEngine.LoadFontFace failed for [" + candidatePath + "] during " + reason + ": " + error);
            return false;
        }
        catch (Exception ex)
        {
            Log("FontEngine.LoadFontFace threw for [" + candidatePath + "] during " + reason + ": " + ex.Message);
            return false;
        }
    }

    private static TMP_FontAsset CreateEmptyFontAsset()
    {
        TMP_FontAsset fontAsset = ScriptableObject.CreateInstance<TMP_FontAsset>();
        fontAsset.faceInfo = FontEngine.GetFaceInfo();
        fontAsset.isMultiAtlasTexturesEnabled = false;
        fontAsset.fallbackFontAssetTable = new List<TMP_FontAsset>();

        VersionField?.SetValue(fontAsset, "1.1.0");
        ClearDynamicDataOnBuildField?.SetValue(fontAsset, false);
        AtlasWidthField?.SetValue(fontAsset, AtlasSize);
        AtlasHeightField?.SetValue(fontAsset, AtlasSize);
        AtlasPaddingField?.SetValue(fontAsset, AtlasPadding);
        AtlasRenderModeField?.SetValue(fontAsset, GlyphRenderMode.SDFAA);
        AtlasPopulationModeField?.SetValue(fontAsset, AtlasPopulationMode.Static);
        AtlasTextureIndexField?.SetValue(fontAsset, 0);
        GlyphTableField?.SetValue(fontAsset, new List<Glyph>());
        CharacterTableField?.SetValue(fontAsset, new List<TMP_Character>());
        GlyphLookupDictionaryField?.SetValue(fontAsset, new Dictionary<uint, Glyph>());
        CharacterLookupDictionaryField?.SetValue(fontAsset, new Dictionary<uint, TMP_Character>());
        FontFeatureTableField?.SetValue(fontAsset, new TMP_FontFeatureTable());
        FontWeightTableField?.SetValue(fontAsset, new TMP_FontWeightPair[10]);
        FreeGlyphRectsField?.SetValue(fontAsset, new List<GlyphRect> { new GlyphRect(0, 0, AtlasSize - 1, AtlasSize - 1) });
        UsedGlyphRectsField?.SetValue(fontAsset, new List<GlyphRect>());

        Texture2D atlasTexture = new Texture2D(AtlasSize, AtlasSize, TextureFormat.Alpha8, false);
        atlasTexture.filterMode = FilterMode.Bilinear;
        atlasTexture.wrapMode = TextureWrapMode.Clamp;
        atlasTexture.name = "Hanpaemo Korean Fallback Atlas";
        ResetAtlasTexture(atlasTexture);

        AtlasTexturesField?.SetValue(fontAsset, new[] { atlasTexture });
        AtlasTextureField?.SetValue(fontAsset, atlasTexture);
        fontAsset.atlas = atlasTexture;

        Material material = CreateFontMaterial(fontAsset, atlasTexture);
        fontAsset.material = material;

        fontAsset.ReadFontAssetDefinition();
        return fontAsset;
    }

    private static Material CreateFontMaterial(TMP_FontAsset fontAsset, Texture2D atlasTexture)
    {
        Material templateMaterial = Resources.FindObjectsOfTypeAll<TMP_FontAsset>()
            .Select(asset => asset != null ? asset.material : null)
            .FirstOrDefault(material => material != null && material.shader != null);

        if (templateMaterial == null)
        {
            throw new InvalidOperationException("No loaded TMP material template was available.");
        }

        Material material = new Material(templateMaterial);

        material.name = "Hanpaemo Korean Fallback Material";
        material.SetTexture(ShaderUtilities.ID_MainTex, atlasTexture);
        material.SetFloat(ShaderUtilities.ID_TextureWidth, AtlasSize);
        material.SetFloat(ShaderUtilities.ID_TextureHeight, AtlasSize);
        material.SetFloat(ShaderUtilities.ID_GradientScale, AtlasPadding + 1);
        material.SetFloat(ShaderUtilities.ID_WeightNormal, fontAsset.normalStyle);
        material.SetFloat(ShaderUtilities.ID_WeightBold, fontAsset.boldStyle);

        return material;
    }

    private static void ResetAtlasTexture(Texture2D atlasTexture)
    {
        if (ResetAtlasTextureMethod != null)
        {
            ResetAtlasTextureMethod.Invoke(null, new object[] { atlasTexture });
            return;
        }

        Color32[] clearPixels = new Color32[AtlasSize * AtlasSize];
        atlasTexture.SetPixels32(clearPixels);
        atlasTexture.Apply(false, false);
    }

    private static List<GlyphRect> GetFreeGlyphRects(TMP_FontAsset fontAsset)
    {
        return (List<GlyphRect>)FreeGlyphRectsField.GetValue(fontAsset);
    }

    private static List<GlyphRect> GetUsedGlyphRects(TMP_FontAsset fontAsset)
    {
        return (List<GlyphRect>)UsedGlyphRectsField.GetValue(fontAsset);
    }

    private static List<Glyph> GetGlyphTable(TMP_FontAsset fontAsset)
    {
        return (List<Glyph>)GlyphTableField.GetValue(fontAsset);
    }

    private static List<TMP_Character> GetCharacterTable(TMP_FontAsset fontAsset)
    {
        return (List<TMP_Character>)CharacterTableField.GetValue(fontAsset);
    }

    private static Dictionary<uint, Glyph> GetGlyphLookup(TMP_FontAsset fontAsset)
    {
        return (Dictionary<uint, Glyph>)GlyphLookupDictionaryField.GetValue(fontAsset);
    }

    private static Dictionary<uint, TMP_Character> GetCharacterLookup(TMP_FontAsset fontAsset)
    {
        return (Dictionary<uint, TMP_Character>)CharacterLookupDictionaryField.GetValue(fontAsset);
    }

    private static GlyphRenderMode GetAtlasRenderMode(TMP_FontAsset fontAsset)
    {
        object value = AtlasRenderModeField.GetValue(fontAsset);
        return value is GlyphRenderMode renderMode ? renderMode : GlyphRenderMode.SDFAA;
    }

    private static bool InvokeTryAddGlyphsToTexture(
        List<uint> glyphIndexesToPack,
        List<GlyphRect> freeGlyphRects,
        List<GlyphRect> usedGlyphRects,
        GlyphRenderMode renderMode,
        Texture2D atlasTexture,
        out Glyph[] packedGlyphs)
    {
        if (TryAddGlyphsToTextureMethod == null)
        {
            packedGlyphs = Array.Empty<Glyph>();
            Log("FontEngine.TryAddGlyphsToTexture reflection method was not found.");
            return false;
        }

        object[] args =
        {
            glyphIndexesToPack,
            AtlasPadding,
            GlyphPackingMode.BestShortSideFit,
            freeGlyphRects,
            usedGlyphRects,
            renderMode,
            atlasTexture,
            null,
        };

        bool result = (bool)TryAddGlyphsToTextureMethod.Invoke(null, args);
        packedGlyphs = (Glyph[])args[7] ?? Array.Empty<Glyph>();
        return result;
    }

    private static string BuildBootstrapCharacterSet()
    {
        return NormalizeCharacters(ProbeText);
    }

    private static string BuildRequiredCharacterSet()
    {
        string requiredPath = Path.Combine(ManagedDirectory, RequiredCharsetFileName);
        string raw = string.Empty;

        if (File.Exists(requiredPath))
        {
            try
            {
                raw = File.ReadAllText(requiredPath);
                Log("Loaded required charset file [" + requiredPath + "].");
            }
            catch (Exception ex)
            {
                Log("Failed to read required charset file [" + requiredPath + "]: " + ex.Message);
            }
        }

        return NormalizeCharacters(raw + ProbeText);
    }

    private static bool TryAddCharactersToFallback(TMP_FontAsset fontAsset, string candidatePath, string characters, string reason)
    {
        string normalized = NormalizeCharacters(characters);
        if (string.IsNullOrEmpty(normalized))
        {
            return true;
        }

        if (!TryLoadFontFace(candidatePath, "add:" + reason))
        {
            return false;
        }

        List<GlyphRect> freeGlyphRects = GetFreeGlyphRects(fontAsset);
        List<GlyphRect> usedGlyphRects = GetUsedGlyphRects(fontAsset);
        List<Glyph> glyphTable = GetGlyphTable(fontAsset);
        List<TMP_Character> characterTable = GetCharacterTable(fontAsset);
        Dictionary<uint, Glyph> glyphLookup = GetGlyphLookup(fontAsset);
        Dictionary<uint, TMP_Character> characterLookup = GetCharacterLookup(fontAsset);

        Dictionary<uint, uint> glyphIndexByUnicode = new();
        List<uint> glyphIndexesToPack = new();
        List<uint> missingUnicodes = new();

        foreach (char ch in normalized)
        {
            uint unicode = ch;
            if (fontAsset.characterLookupTable.ContainsKey(unicode))
            {
                continue;
            }

            Glyph lookupGlyph;
            if (!FontEngine.TryGetGlyphWithUnicodeValue(unicode, GlyphLoadFlags.LOAD_RENDER, out lookupGlyph))
            {
                missingUnicodes.Add(unicode);
                continue;
            }

            glyphIndexByUnicode[unicode] = lookupGlyph.index;
            if (!glyphIndexesToPack.Contains(lookupGlyph.index))
            {
                glyphIndexesToPack.Add(lookupGlyph.index);
            }
        }

        if (glyphIndexesToPack.Count == 0)
        {
            if (missingUnicodes.Count > 0)
            {
                Log("No packable glyphs found for reason [" + reason + "]. Missing from source font: " + missingUnicodes.Count);
            }

            return missingUnicodes.Count == 0;
        }

        Glyph[] packedGlyphs;
        bool allAdded = InvokeTryAddGlyphsToTexture(
            glyphIndexesToPack,
            freeGlyphRects,
            usedGlyphRects,
            GetAtlasRenderMode(fontAsset),
            fontAsset.atlasTextures[0],
            out packedGlyphs);

        HashSet<uint> addedGlyphIndexes = new();
        foreach (Glyph glyph in packedGlyphs ?? Array.Empty<Glyph>())
        {
            if (glyph == null)
            {
                continue;
            }

            glyph.atlasIndex = 0;
            if (!glyphLookup.ContainsKey(glyph.index))
            {
                glyphTable.Add(glyph);
                glyphLookup[glyph.index] = glyph;
                addedGlyphIndexes.Add(glyph.index);
            }
        }

        int addedCharacters = 0;
        foreach (KeyValuePair<uint, uint> pair in glyphIndexByUnicode)
        {
            uint unicode = pair.Key;
            uint glyphIndex = pair.Value;

            if (!glyphLookup.TryGetValue(glyphIndex, out Glyph glyph))
            {
                continue;
            }

            if (characterLookup.ContainsKey(unicode))
            {
                continue;
            }

            TMP_Character character = new TMP_Character(unicode, fontAsset, glyph);
            characterTable.Add(character);
            characterLookup[unicode] = character;
            addedCharacters++;
        }

        glyphTable.Sort((left, right) => left.index.CompareTo(right.index));
        characterTable.Sort((left, right) => left.unicode.CompareTo(right.unicode));
        fontAsset.atlasTextures[0].Apply(false, false);
        fontAsset.ReadFontAssetDefinition();

        int unresolved = glyphIndexByUnicode.Count(kvp => !characterLookup.ContainsKey(kvp.Key)) + missingUnicodes.Count;
        Log(
            "Manual glyph bake ["
                + reason
                + "] from ["
                + candidatePath
                + "]: requested="
                + glyphIndexesToPack.Count
                + ", addedGlyphs="
                + addedGlyphIndexes.Count
                + ", addedCharacters="
                + addedCharacters
                + ", unresolved="
                + unresolved
                + ", allAdded="
                + allAdded
        );

        return addedCharacters > 0;
    }

    private static string NormalizeCharacters(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        HashSet<char> seen = new();
        List<char> result = new(text.Length);
        foreach (char ch in text)
        {
            if (char.IsControl(ch))
            {
                continue;
            }

            if (!seen.Add(ch))
            {
                continue;
            }

            result.Add(ch);
        }

        return new string(result.ToArray());
    }

    private static void AttachGlobalFallback()
    {
        if (fallbackFont == null)
        {
            return;
        }

        List<TMP_FontAsset> fallbackList = TMP_Settings.fallbackFontAssets;
        if (fallbackList != null && !fallbackList.Contains(fallbackFont))
        {
            fallbackList.Add(fallbackFont);
            Log("Added fallback font to TMP_Settings.");
        }
    }

    private static void PatchLoadedFontAssets()
    {
        if (fallbackFont == null)
        {
            return;
        }

        foreach (TMP_FontAsset font in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
        {
            if (font == null || font == fallbackFont)
            {
                continue;
            }

            if (font.fallbackFontAssetTable == null)
            {
                font.fallbackFontAssetTable = new List<TMP_FontAsset>();
            }

            if (!font.fallbackFontAssetTable.Contains(fallbackFont))
            {
                font.fallbackFontAssetTable.Add(fallbackFont);
            }
        }
    }

    private static void WarmupRequiredCharacters()
    {
        if (fallbackFont == null || string.IsNullOrEmpty(activeFontSourcePath))
        {
            return;
        }

        string requiredCharacters = BuildRequiredCharacterSet();
        if (string.IsNullOrEmpty(requiredCharacters))
        {
            return;
        }

        TryAddCharactersToFallback(fallbackFont, activeFontSourcePath, requiredCharacters, "warmup");
    }

    private static void RefreshTexts(string reason)
    {
        if (fallbackFont == null)
        {
            return;
        }

        int textCount = 0;
        foreach (TMP_Text text in Resources.FindObjectsOfTypeAll(typeof(TMP_Text)).OfType<TMP_Text>())
        {
            if (text == null)
            {
                continue;
            }

            PatchText(text);
            textCount++;
        }

        Log("RefreshTexts(" + reason + ") patched TMP text objects: " + textCount);
    }

    private static void PatchText(TMP_Text text)
    {
        if (text.font == null)
        {
            text.font = fallbackFont;
        }
        else if (text.font != fallbackFont)
        {
            if (text.font.fallbackFontAssetTable == null)
            {
                text.font.fallbackFontAssetTable = new List<TMP_FontAsset>();
            }

            if (!text.font.fallbackFontAssetTable.Contains(fallbackFont))
            {
                text.font.fallbackFontAssetTable.Add(fallbackFont);
            }
        }

        string interestingText = ExtractInterestingText(text.text);
        if (!string.IsNullOrEmpty(interestingText) && !string.IsNullOrEmpty(activeFontSourcePath))
        {
            TryAddCharactersToFallback(fallbackFont, activeFontSourcePath, interestingText, "text:" + SafeName(text.name));
        }

        text.havePropertiesChanged = true;
        text.SetVerticesDirty();
        text.SetLayoutDirty();
    }

    private static string ExtractInterestingText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        List<char> chars = new(text.Length);
        bool insideTag = false;
        foreach (char ch in text)
        {
            if (ch == '<')
            {
                insideTag = true;
                continue;
            }

            if (insideTag)
            {
                if (ch == '>')
                {
                    insideTag = false;
                }

                continue;
            }

            if (!char.IsControl(ch))
            {
                chars.Add(ch);
            }
        }

        return NormalizeCharacters(new string(chars.ToArray()));
    }

    private static string SafeName(string name)
    {
        return string.IsNullOrWhiteSpace(name) ? "<unnamed>" : name;
    }

    internal static void Log(string message)
    {
        try
        {
            Debug.Log("[Hanpaemo][GUNTOUCHABLES] " + message);
        }
        catch
        {
        }
    }
}

public sealed class KoreanFontDriver : MonoBehaviour
{
    private string lastLocaleCode = string.Empty;
    private int refreshBursts;
    private float nextRefreshAt;

    private void Awake()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        TriggerRefresh("driver-awake");
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        KoreanFontRuntime.ClearDriver(this);
    }

    private void Update()
    {
        string currentLocaleCode = GetLocaleCode();
        if (currentLocaleCode != lastLocaleCode)
        {
            lastLocaleCode = currentLocaleCode;
            TriggerRefresh("locale:" + currentLocaleCode);
        }

        if (refreshBursts <= 0 || Time.unscaledTime < nextRefreshAt)
        {
            return;
        }

        KoreanFontRuntime.TryInstall("burst");
        refreshBursts--;
        nextRefreshAt = Time.unscaledTime + (refreshBursts > 1 ? 0.25f : 0.75f);
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        TriggerRefresh("scene:" + scene.name);
    }

    internal void TriggerRefresh(string reason)
    {
        refreshBursts = 4;
        nextRefreshAt = 0f;
        KoreanFontRuntime.Log("Scheduled refresh: " + reason);
        KoreanFontRuntime.TryInstall(reason + "-immediate");
    }

    private static string GetLocaleCode()
    {
        try
        {
            if (LocalizationSettings.SelectedLocale == null)
            {
                return string.Empty;
            }

            return LocalizationSettings.SelectedLocale.Identifier.Code ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
