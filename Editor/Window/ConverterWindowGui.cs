using System;
using UnityEditor;
using UnityEngine;

internal static class ConverterWindowGui
{
    internal static void DrawOutlinedSetting(Action draw)
    {
        if (draw == null) return;
        Rect border = EditorGUILayout.BeginVertical();
        try
        {
            GUILayout.Space(5f);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(7f);
                using (new EditorGUILayout.VerticalScope()) draw();
                GUILayout.Space(7f);
            }
            GUILayout.Space(5f);
        }
        finally
        {
            EditorGUILayout.EndVertical();
        }
        DrawOutline(border);
        EditorGUILayout.Space(4f);
    }

    internal static void DrawSectionTitle(string title)
    {
        EditorGUILayout.Space(4f);
        GUILayout.Label(title, new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = 16,
            alignment = TextAnchor.MiddleCenter
        });
        EditorGUILayout.Space(6f);
    }

    internal static void DrawCenteredLabel(string text, GUIStyle style)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            GUILayout.Label(text, style);
            GUILayout.FlexibleSpace();
        }
    }

    internal static void DrawEntrySeparator()
    {
        Rect rect = EditorGUILayout.GetControlRect(false, 5f);
        Color color = EditorGUIUtility.isProSkin
            ? new Color(1f, 1f, 1f, 0.20f)
            : new Color(0f, 0f, 0f, 0.24f);
        EditorGUI.DrawRect(new Rect(rect.x, rect.y + 2f, rect.width, 1f), color);
    }

    internal static void DrawOutline(Rect rect)
    {
        if (Event.current.type != EventType.Repaint || rect.width <= 0f || rect.height <= 0f)
            return;

        Color color = EditorGUIUtility.isProSkin
            ? new Color(0.48f, 0.48f, 0.48f, 1f)
            : new Color(0.42f, 0.42f, 0.42f, 1f);
        EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1f), color);
        EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), color);
        EditorGUI.DrawRect(new Rect(rect.x, rect.y, 1f, rect.height), color);
        EditorGUI.DrawRect(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), color);
    }
}
