using UnityEditor;
using UnityEngine;

public sealed class MainConverterWindow : EditorWindow
{
    private const string MenuPath = "VeraFirefly/VMD Converter";
    private const string PackageRoot = "Packages/com.vrchat.world.mmdconverter";
    private const string IconPath = PackageRoot + "/Editor/Icon/icon.png";
    private const string AuthorName = "VeraFirefly";

    [SerializeField] private int selectedTab;
    [SerializeField] private ConverterWindowContext windowContext = new ConverterWindowContext();
    [SerializeField] private VmdConverterPage vmdConverterPage = new VmdConverterPage();
    [SerializeField] private VmdCameraPage vmdCameraPage = new VmdCameraPage();
    [SerializeField] private PmxSettingsPage pmxSettingsPage = new PmxSettingsPage();

    [MenuItem(MenuPath)]
    private static void Open()
    {
        MainConverterWindow window = GetWindow<MainConverterWindow>("VMD Converter");
        window.Show();
    }

    private void OnEnable()
    {
        EnsureFeaturePages();
        titleContent = new GUIContent(
            "VMD Converter",
            AssetDatabase.LoadAssetAtPath<Texture2D>(IconPath));
        minSize = new Vector2(430f, 420f);

        DefaultAsset selected = Selection.activeObject as DefaultAsset;
        vmdConverterPage.OnEnable(windowContext, selected, selectedTab == 0);
        vmdCameraPage.OnEnable(windowContext, selected, selectedTab == 1);
        pmxSettingsPage.OnEnable(windowContext);
    }

    private void OnHierarchyChange()
    {
        EnsureFeaturePages();
        vmdConverterPage.OnHierarchyChange();
        Repaint();
    }

    private void OnDisable()
    {
        if (vmdConverterPage != null)
            vmdConverterPage.OnDisable();
    }

    private void OnProjectChange()
    {
        EnsureFeaturePages();
        vmdConverterPage.OnProjectChange();
        Repaint();
    }

    private void OnGUI()
    {
        EnsureFeaturePages();
        DrawHeader();
        selectedTab = GUILayout.Toolbar(
            selectedTab,
            new[]
            {
                L("VMD动作转换", "VMD Motion", "VMDモーション変換"),
                L("VMD镜头转换", "VMD Camera", "VMDカメラ変換"),
                L("PMX转换设置", "PMX Settings", "PMX変換設定")
            });
        EditorGUILayout.Space(8f);

        using (new EditorGUILayout.VerticalScope(GUILayout.ExpandHeight(true)))
        {
            switch (selectedTab)
            {
                case 0:
                    vmdConverterPage.Draw(windowContext);
                    break;
                case 1:
                    vmdCameraPage.Draw(windowContext);
                    break;
                default:
                    pmxSettingsPage.Draw(windowContext);
                    break;
            }
        }

        if (selectedTab == 0)
            vmdConverterPage.DrawAction(windowContext);
        else if (selectedTab == 1)
            vmdCameraPage.DrawAction(windowContext);
        EditorGUILayout.Space(4f);
    }

    private void EnsureFeaturePages()
    {
        if (windowContext == null) windowContext = new ConverterWindowContext();
        if (vmdConverterPage == null) vmdConverterPage = new VmdConverterPage();
        if (vmdCameraPage == null) vmdCameraPage = new VmdCameraPage();
        if (pmxSettingsPage == null) pmxSettingsPage = new PmxSettingsPage();

        vmdConverterPage.Attach(windowContext);
        vmdCameraPage.Attach(windowContext);
        pmxSettingsPage.Attach(windowContext);
    }

    private void DrawHeader()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            Texture2D icon = AssetDatabase.LoadAssetAtPath<Texture2D>(IconPath);
            if (icon != null)
                GUILayout.Label(icon, GUILayout.Width(56f), GUILayout.Height(56f));
            GUILayout.Label("VMD Converter", new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 19,
                alignment = TextAnchor.MiddleCenter
            }, GUILayout.Height(56f));
            GUILayout.FlexibleSpace();
        }

        ConverterWindowGui.DrawCenteredLabel(
            L("版本号: ", "Version: ", "バージョン: ") + GetPackageVersion(),
            EditorStyles.centeredGreyMiniLabel);
        ConverterWindowGui.DrawCenteredLabel(
            L("作者: ", "Author: ", "作者: ") + AuthorName,
            EditorStyles.centeredGreyMiniLabel);

        ConverterEditorLanguage nextLanguage = (ConverterEditorLanguage)EditorGUILayout.Popup(
            L("语言", "Language", "言語"),
            (int)windowContext.Language,
            new[] { "简体中文", "English", "日本語" });
        if (nextLanguage != windowContext.Language)
        {
            windowContext.Language = nextLanguage;
            vmdConverterPage.OnLanguageChanged();
            Repaint();
        }
        EditorGUILayout.Space(6f);
    }

    private string L(string chinese, string english, string japanese)
    {
        return windowContext.L(chinese, english, japanese);
    }

    private static string GetPackageVersion()
    {
        UnityEditor.PackageManager.PackageInfo package =
            UnityEditor.PackageManager.PackageInfo.FindForAssetPath(PackageRoot);
        if (string.IsNullOrWhiteSpace(package?.version)) return "Unknown";
        return package.version == "0.1.0" ? "0.1" : package.version;
    }
}
