using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

[InitializeOnLoad]
internal static class ConverterPackageValidator
{
    private const string PackageAssetPath = "Packages/com.vrchat.world.mmdconverter";
    private const string QuarantineAssetRoot = "Assets/VMDConverter_Quarantine";
    private const string QuarantineSuffix = ".vmdconverter-quarantine";
    private const int DialogPathLimit = 24;

    // ConverterPackageValidator is deliberately excluded: it is the checker, not package payload.
    private static readonly HashSet<string> ValidatorFiles = new HashSet<string>(StringComparer.Ordinal)
    {
        "Editor/ConverterPackageValidator.cs",
        "Editor/ConverterPackageValidator.cs.meta"
    };

    // Development tools are permitted in a source checkout, but Unity Package Exporter may
    // omit Tools~. They therefore participate in the allowlist without becoming required files.
    private static readonly HashSet<string> OptionalDevelopmentFiles =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Tools~/generate-reference-pmx.mjs"
        };

    // One complete package manifest. There is intentionally no current/legacy split.
    private static readonly HashSet<string> PackageFiles = new HashSet<string>(StringComparer.Ordinal)
    {
        "Editor.meta",
        "Editor/com.vrchat.world.mmdconverter.editor.asmdef",
        "Editor/com.vrchat.world.mmdconverter.editor.asmdef.meta",
        "Editor/Features.meta",
        "Editor/Features/PmxSettingsPage.cs",
        "Editor/Features/PmxSettingsPage.cs.meta",
        "Editor/Features/VmdCameraPage.cs",
        "Editor/Features/VmdCameraPage.cs.meta",
        "Editor/Features/VmdConverterPage.cs",
        "Editor/Features/VmdConverterPage.cs.meta",
        "Editor/Icon.meta",
        "Editor/Icon/icon.png",
        "Editor/Icon/icon.png.meta",
        "Editor/MotionConverter.meta",
        "Editor/MotionConverter/CameraConverter.meta",
        "Editor/MotionConverter/CameraConverter/VmdCameraConverter.cs",
        "Editor/MotionConverter/CameraConverter/VmdCameraConverter.cs.meta",
        "Editor/MotionConverter/Model.meta",
        "Editor/MotionConverter/Model/LICENSE.txt",
        "Editor/MotionConverter/Model/LICENSE.txt.meta",
        "Editor/MotionConverter/Model/NOTICE.txt",
        "Editor/MotionConverter/Model/NOTICE.txt.meta",
        "Editor/MotionConverter/Model/README.md",
        "Editor/MotionConverter/Model/README.md.meta",
        "Editor/MotionConverter/Model/_model.pmx",
        "Editor/MotionConverter/Model/_model.pmx.meta",
        "Editor/MotionConverter/VmdConverter.meta",
        "Editor/MotionConverter/VmdConverter/VmdMotionConversionData.cs",
        "Editor/MotionConverter/VmdConverter/VmdMotionConversionData.cs.meta",
        "Editor/MotionConverter/VmdConverter/VmdMotionConverter.cs",
        "Editor/MotionConverter/VmdConverter/VmdMotionConverter.cs.meta",
        "Editor/Pmx2Fbx.meta",
        "Editor/Pmx2Fbx/PmxAnimationClipBaker.cs",
        "Editor/Pmx2Fbx/PmxAnimationClipBaker.cs.meta",
        "Editor/Pmx2Fbx/PmxAnimationConversion.cs",
        "Editor/Pmx2Fbx/PmxAnimationConversion.cs.meta",
        "Editor/Pmx2Fbx/PmxAnimationConversionTypes.cs",
        "Editor/Pmx2Fbx/PmxAnimationConversionTypes.cs.meta",
        "Editor/Pmx2Fbx/PmxHumanoidAnimationClipBaker.cs",
        "Editor/Pmx2Fbx/PmxHumanoidAnimationClipBaker.cs.meta",
        "Editor/Pmx2Fbx/PmxHumanoidPoseSample.cs",
        "Editor/Pmx2Fbx/PmxHumanoidPoseSample.cs.meta",
        "Editor/Pmx2Fbx/PmxHumanoidSourceRig.cs",
        "Editor/Pmx2Fbx/PmxHumanoidSourceRig.cs.meta",
        "Editor/Pmx2Fbx/PmxModelCache.cs",
        "Editor/Pmx2Fbx/PmxModelCache.cs.meta",
        "Editor/Pmx2Fbx/PmxTargetFkAnimationClipBaker.cs",
        "Editor/Pmx2Fbx/PmxTargetFkAnimationClipBaker.cs.meta",
        "Editor/Pmx2Fbx/PmxTargetLowerBodyFkAnimationClipBaker.cs",
        "Editor/Pmx2Fbx/PmxTargetLowerBodyFkAnimationClipBaker.cs.meta",
        "Editor/Pmx2Fbx/PmxTargetHumanoidAnimationClipBaker.cs",
        "Editor/Pmx2Fbx/PmxTargetHumanoidAnimationClipBaker.cs.meta",
        "Editor/Pmx2Fbx/PmxTargetHumanoidSourceRig.cs",
        "Editor/Pmx2Fbx/PmxTargetHumanoidSourceRig.cs.meta",
        "Editor/Window.meta",
        "Editor/Window/ConverterWindowContext.cs",
        "Editor/Window/ConverterWindowContext.cs.meta",
        "Editor/Window/ConverterWindowGui.cs",
        "Editor/Window/ConverterWindowGui.cs.meta",
        "Editor/Window/MainConverterWindow.cs",
        "Editor/Window/MainConverterWindow.cs.meta",
        "LICENSE",
        "LICENSE.meta",
        "README-ZH-rCN.md",
        "README-ZH-rCN.md.meta",
        "README.md",
        "README.md.meta",
        "Runtime.meta",
        "Runtime/com.vrchat.world.mmdconverter.asmdef",
        "Runtime/com.vrchat.world.mmdconverter.asmdef.meta",
        "Runtime/Pmx2Fbx.meta",
        "Runtime/Pmx2Fbx/PMX.meta",
        "Runtime/Pmx2Fbx/PMX/PmxModel.cs",
        "Runtime/Pmx2Fbx/PMX/PmxModel.cs.meta",
        "Runtime/Pmx2Fbx/PMX/PmxParser.cs",
        "Runtime/Pmx2Fbx/PMX/PmxParser.cs.meta",
        "Runtime/Pmx2Fbx/Solver.meta",
        "Runtime/Pmx2Fbx/Solver/PmxMotionEvaluator.cs",
        "Runtime/Pmx2Fbx/Solver/PmxMotionEvaluator.cs.meta",
        "Runtime/Pmx2Fbx/Solver/PmxPose.cs",
        "Runtime/Pmx2Fbx/Solver/PmxPose.cs.meta",
        "Runtime/Pmx2Fbx/VMD.meta",
        "Runtime/Pmx2Fbx/VMD/MmdBezierCurve.cs",
        "Runtime/Pmx2Fbx/VMD/MmdBezierCurve.cs.meta",
        "Runtime/Pmx2Fbx/VMD/VmdMotion.cs",
        "Runtime/Pmx2Fbx/VMD/VmdMotion.cs.meta",
        "Runtime/Pmx2Fbx/VMD/VmdParser.cs",
        "Runtime/Pmx2Fbx/VMD/VmdParser.cs.meta",
        "Runtime/Pmx2Fbx/VMD/VmdTrackSampler.cs",
        "Runtime/Pmx2Fbx/VMD/VmdTrackSampler.cs.meta",
        "package.json",
        "package.json.meta"
    };

    private static bool validationScheduled;
    private static string lastReportedIssueSignature;

    static ConverterPackageValidator()
    {
        ScheduleValidation();
    }

    internal static void ScheduleValidation()
    {
        if (validationScheduled)
            return;

        validationScheduled = true;
        EditorApplication.delayCall += ValidatePackage;
    }

    private static void ValidatePackage()
    {
        validationScheduled = false;

        if (!TryGetPackageRoot(out string packageRoot))
            return;

        string[] actualFiles = Directory
            .EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
            .Select(path => NormalizeRelativePath(packageRoot, path))
            .Where(path => !ValidatorFiles.Contains(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        string[] unexpectedFiles = actualFiles
            .Where(path => !PackageFiles.Contains(path) &&
                           !OptionalDevelopmentFiles.Contains(path))
            .ToArray();
        string[] missingFiles = PackageFiles
            .Where(path => !actualFiles.Contains(path, StringComparer.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        if (unexpectedFiles.Length == 0 && missingFiles.Length == 0)
        {
            lastReportedIssueSignature = null;
            return;
        }

        string issueSignature = string.Join("\n", unexpectedFiles) + "\n--missing--\n" +
                                string.Join("\n", missingFiles);
        if (string.Equals(lastReportedIssueSignature, issueSignature, StringComparison.Ordinal))
            return;

        lastReportedIssueSignature = issueSignature;
        string message = BuildMessage(
            unexpectedFiles, missingFiles, GetPackageVersion(packageRoot));
        Debug.LogError("[VMD Converter] Package self-check failed.\n" + message);

        if (Application.isBatchMode)
            return;

        if (unexpectedFiles.Length > 0)
        {
            int choice = EditorUtility.DisplayDialogComplex(
                "VMD Converter - 包自检失败",
                message,
                "安全剔除",
                "仅关闭",
                "打开包目录");

            if (choice == 0)
                QuarantineUnexpectedFiles(packageRoot, unexpectedFiles);
            else if (choice == 2)
                EditorUtility.RevealInFinder(packageRoot);
        }
        else
        {
            int choice = EditorUtility.DisplayDialogComplex(
                "VMD Converter - 包自检失败",
                message,
                "仅关闭",
                "重新检查",
                "打开包目录");

            if (choice == 1)
            {
                lastReportedIssueSignature = null;
                ScheduleValidation();
            }
            else if (choice == 2)
            {
                EditorUtility.RevealInFinder(packageRoot);
            }
        }
    }

    private static bool TryGetPackageRoot(out string packageRoot)
    {
        PackageInfo package = PackageInfo.FindForAssembly(typeof(ConverterPackageValidator).Assembly);
        if (package != null && !string.IsNullOrWhiteSpace(package.resolvedPath))
        {
            packageRoot = Path.GetFullPath(package.resolvedPath);
            if (Directory.Exists(packageRoot))
                return true;
        }

        packageRoot = Path.GetFullPath(PackageAssetPath);
        return Directory.Exists(packageRoot);
    }

    private static void QuarantineUnexpectedFiles(string packageRoot, string[] reportedUnexpectedFiles)
    {
        var movedFiles = new List<QuarantinedFile>();
        string quarantineRoot = null;
        string reportPath = null;

        try
        {
            string assetsRoot = Path.GetFullPath("Assets");
            if (!Directory.Exists(assetsRoot))
                throw new DirectoryNotFoundException("找不到项目 Assets 目录。");

            if (!HaveSameVolume(packageRoot, assetsRoot))
                throw new IOException("包目录与 Assets 不在同一磁盘，无法保证原子移动，已取消剔除。");

            string[] confirmedUnexpectedFiles = RevalidateUnexpectedFiles(packageRoot, reportedUnexpectedFiles);
            if (confirmedUnexpectedFiles.Length == 0)
            {
                lastReportedIssueSignature = null;
                ScheduleValidation();
                return;
            }

            quarantineRoot = CreateUniqueQuarantineRoot(assetsRoot);
            foreach (string relativePath in confirmedUnexpectedFiles)
            {
                string sourcePath = GetValidatedPackageFilePath(packageRoot, relativePath);
                string destinationPath = Path.Combine(
                    quarantineRoot,
                    relativePath.Replace('/', Path.DirectorySeparatorChar) + QuarantineSuffix);

                string destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (string.IsNullOrEmpty(destinationDirectory))
                    throw new InvalidDataException("无法确定隔离目标目录：" + relativePath);

                Directory.CreateDirectory(destinationDirectory);
                if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
                    throw new IOException("隔离目标已存在，未覆盖：" + destinationPath);

                long length = new FileInfo(sourcePath).Length;
                File.Move(sourcePath, destinationPath);
                movedFiles.Add(new QuarantinedFile(relativePath, sourcePath, destinationPath, length));
            }

            reportPath = Path.Combine(quarantineRoot, "QuarantineReport.txt");
            File.WriteAllText(reportPath, BuildQuarantineReport(packageRoot, quarantineRoot, movedFiles), Encoding.UTF8);
            RemoveUnexpectedEmptyDirectories(packageRoot);

            lastReportedIssueSignature = null;
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            ScheduleValidation();

            string quarantineAssetPath = ToProjectRelativeAssetPath(quarantineRoot);
            bool confirmed = EditorUtility.DisplayDialog(
                "VMD Converter - 安全剔除完成",
                $"已隔离 {movedFiles.Count} 个文件。\n\n位置：{quarantineAssetPath}\n所有文件均追加了 {QuarantineSuffix} 后缀，不会按原类型参与编译或加载。",
                "确定",
                "打开隔离目录");
            if (!confirmed)
                EditorUtility.RevealInFinder(quarantineRoot);
        }
        catch (Exception exception)
        {
            string rollbackError = RollBackMovedFiles(movedFiles);
            TryDeleteIncompleteReport(reportPath);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            string detail = string.IsNullOrEmpty(rollbackError)
                ? "已回滚本次已经移动的文件。"
                : "部分文件回滚失败，请根据以下信息手动恢复：\n" + rollbackError;
            Debug.LogError("[VMD Converter] Safe quarantine failed.\n" + exception + "\n" + detail);
            EditorUtility.DisplayDialog(
                "VMD Converter - 安全剔除失败",
                exception.Message + "\n\n" + detail,
                "确定");
        }
    }

    private static string[] RevalidateUnexpectedFiles(string packageRoot, string[] reportedUnexpectedFiles)
    {
        var confirmed = new List<string>();
        foreach (string relativePath in reportedUnexpectedFiles.Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(relativePath) ||
                PackageFiles.Contains(relativePath) ||
                OptionalDevelopmentFiles.Contains(relativePath) ||
                ValidatorFiles.Contains(relativePath))
            {
                continue;
            }

            string sourcePath = GetValidatedPackageFilePath(packageRoot, relativePath);
            if (File.Exists(sourcePath))
                confirmed.Add(relativePath);
        }

        confirmed.Sort(StringComparer.Ordinal);
        return confirmed.ToArray();
    }

    private static string GetValidatedPackageFilePath(string packageRoot, string relativePath)
    {
        string normalizedRoot = EnsureTrailingSeparator(Path.GetFullPath(packageRoot));
        string fullPath = Path.GetFullPath(Path.Combine(
            packageRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!fullPath.StartsWith(normalizedRoot, PathComparison))
            throw new InvalidDataException("检测到越界路径，已取消剔除：" + relativePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("待隔离文件已经不存在。", fullPath);
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0 ||
            ContainsReparsePointDirectory(packageRoot, Path.GetDirectoryName(fullPath)))
        {
            throw new IOException("检测到符号链接或目录联接，已取消剔除：" + relativePath);
        }

        return fullPath;
    }

    private static bool ContainsReparsePointDirectory(string packageRoot, string directoryPath)
    {
        string normalizedRoot = Path.GetFullPath(packageRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var directory = new DirectoryInfo(directoryPath);

        while (directory != null &&
               !string.Equals(directory.FullName.TrimEnd(Path.DirectorySeparatorChar), normalizedRoot, PathComparison))
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                return true;
            directory = directory.Parent;
        }

        return directory == null;
    }

    private static string CreateUniqueQuarantineRoot(string assetsRoot)
    {
        string baseRoot = Path.Combine(
            assetsRoot,
            "VMDConverter_Quarantine",
            DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        string candidate = baseRoot;
        int suffix = 1;
        while (Directory.Exists(candidate) || File.Exists(candidate))
            candidate = baseRoot + "-" + suffix++;

        Directory.CreateDirectory(candidate);
        return candidate;
    }

    private static string BuildQuarantineReport(
        string packageRoot,
        string quarantineRoot,
        IEnumerable<QuarantinedFile> movedFiles)
    {
        var builder = new StringBuilder();
        builder.AppendLine("VMD Converter quarantine report");
        builder.AppendLine("Time: " + DateTime.Now.ToString("O"));
        builder.AppendLine("Package: " + packageRoot);
        builder.AppendLine("Quarantine: " + quarantineRoot);
        builder.AppendLine();

        foreach (QuarantinedFile file in movedFiles)
        {
            builder.AppendLine("Original: " + file.RelativePath);
            builder.AppendLine("Stored: " + ToProjectRelativeAssetPath(file.DestinationPath));
            builder.AppendLine("Length: " + file.Length + " bytes");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static void RemoveUnexpectedEmptyDirectories(string packageRoot)
    {
        var allowedDirectories = new HashSet<string>(StringComparer.Ordinal) { string.Empty };
        foreach (string relativeFile in PackageFiles
                     .Concat(OptionalDevelopmentFiles)
                     .Concat(ValidatorFiles))
        {
            string directory = Path.GetDirectoryName(relativeFile.Replace('/', Path.DirectorySeparatorChar));
            while (!string.IsNullOrEmpty(directory))
            {
                allowedDirectories.Add(directory.Replace('\\', '/'));
                directory = Path.GetDirectoryName(directory);
            }
        }

        string[] directories = Directory
            .EnumerateDirectories(packageRoot, "*", SearchOption.AllDirectories)
            .OrderByDescending(path => path.Length)
            .ToArray();

        foreach (string directory in directories)
        {
            string relativeDirectory = NormalizeRelativePath(packageRoot, directory);
            if (allowedDirectories.Contains(relativeDirectory) ||
                Directory.EnumerateFileSystemEntries(directory).Any())
            {
                continue;
            }

            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                    Directory.Delete(directory, false);
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "[VMD Converter] Failed to remove empty foreign directory '" +
                    relativeDirectory + "': " + exception.Message);
            }
        }
    }

    private static string RollBackMovedFiles(IReadOnlyList<QuarantinedFile> movedFiles)
    {
        var errors = new List<string>();
        for (int index = movedFiles.Count - 1; index >= 0; index--)
        {
            QuarantinedFile file = movedFiles[index];
            try
            {
                if (!File.Exists(file.DestinationPath))
                    continue;
                if (File.Exists(file.SourcePath))
                    throw new IOException("原位置已有同名文件，未覆盖。");

                string sourceDirectory = Path.GetDirectoryName(file.SourcePath);
                if (!string.IsNullOrEmpty(sourceDirectory))
                    Directory.CreateDirectory(sourceDirectory);
                File.Move(file.DestinationPath, file.SourcePath);
            }
            catch (Exception exception)
            {
                errors.Add(file.RelativePath + "：" + exception.Message);
            }
        }

        return string.Join("\n", errors);
    }

    private static void TryDeleteIncompleteReport(string reportPath)
    {
        if (string.IsNullOrEmpty(reportPath) || !File.Exists(reportPath))
            return;

        try
        {
            File.Delete(reportPath);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[VMD Converter] Failed to remove incomplete quarantine report: " + exception.Message);
        }
    }

    private static bool HaveSameVolume(string firstPath, string secondPath)
    {
        return string.Equals(
            Path.GetPathRoot(Path.GetFullPath(firstPath)),
            Path.GetPathRoot(Path.GetFullPath(secondPath)),
            PathComparison);
    }

    private static string ToProjectRelativeAssetPath(string fullPath)
    {
        string projectRoot = EnsureTrailingSeparator(Path.GetFullPath("."));
        string normalizedPath = Path.GetFullPath(fullPath);
        return normalizedPath.StartsWith(projectRoot, PathComparison)
            ? normalizedPath.Substring(projectRoot.Length).Replace('\\', '/')
            : normalizedPath;
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
               Path.DirectorySeparatorChar;
    }

    private static StringComparison PathComparison =>
        Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed class QuarantinedFile
    {
        internal QuarantinedFile(
            string relativePath,
            string sourcePath,
            string destinationPath,
            long length)
        {
            RelativePath = relativePath;
            SourcePath = sourcePath;
            DestinationPath = destinationPath;
            Length = length;
        }

        internal string RelativePath { get; }
        internal string SourcePath { get; }
        internal string DestinationPath { get; }
        internal long Length { get; }
    }

    private static string NormalizeRelativePath(string packageRoot, string path)
    {
        return path.Substring(packageRoot.Length)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace('\\', '/');
    }

    private static string GetPackageVersion(string packageRoot)
    {
        PackageInfo package = PackageInfo.FindForAssembly(typeof(ConverterPackageValidator).Assembly);
        if (!string.IsNullOrWhiteSpace(package?.version))
            return package.version;

        try
        {
            string manifestPath = Path.Combine(packageRoot, "package.json");
            if (File.Exists(manifestPath))
            {
                PackageManifest manifest = JsonUtility.FromJson<PackageManifest>(
                    File.ReadAllText(manifestPath));
                if (!string.IsNullOrWhiteSpace(manifest?.version))
                    return manifest.version;
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning(
                "[VMD Converter] Could not read package version: " + exception.Message);
        }

        return "Unknown";
    }

    private static string BuildMessage(
        string[] unexpectedFiles, string[] missingFiles, string packageVersion)
    {
        var sections = new List<string>();
        if (unexpectedFiles.Length > 0)
        {
            sections.Add(
                "检测到不属于当前包的文件：\n" +
                FormatPaths(unexpectedFiles));
        }

        if (missingFiles.Length > 0)
        {
            sections.Add(
                "当前包缺少清单中的文件：\n" +
                FormatPaths(missingFiles));
        }

        return string.Join("\n\n", sections) +
               "\n\n请重新安装完整、未混入其他内容的 " + packageVersion + " 包。";
    }

    [Serializable]
    private sealed class PackageManifest
    {
        public string version = string.Empty;
    }

    private static string FormatPaths(string[] paths)
    {
        string visible = string.Join("\n", paths.Take(DialogPathLimit).Select(path => "• " + path));
        int hiddenCount = paths.Length - DialogPathLimit;
        return hiddenCount > 0
            ? visible + $"\n• ……另有 {hiddenCount} 个（完整列表已输出至 Console）"
            : visible;
    }
}

internal sealed class ConverterPackageAssetPostprocessor : AssetPostprocessor
{
    private static void OnPostprocessAllAssets(
        string[] importedAssets,
        string[] deletedAssets,
        string[] movedAssets,
        string[] movedFromAssetPaths)
    {
        if (ContainsPackageAsset(importedAssets) ||
            ContainsPackageAsset(deletedAssets) ||
            ContainsPackageAsset(movedAssets) ||
            ContainsPackageAsset(movedFromAssetPaths))
        {
            ConverterPackageValidator.ScheduleValidation();
        }
    }

    private static bool ContainsPackageAsset(string[] paths)
    {
        return paths != null && paths.Any(path =>
            path.Equals("Packages/com.vrchat.world.mmdconverter", StringComparison.Ordinal) ||
            path.StartsWith("Packages/com.vrchat.world.mmdconverter/", StringComparison.Ordinal));
    }
}
