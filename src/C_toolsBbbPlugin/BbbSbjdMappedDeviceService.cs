using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using C_toolsShared;

namespace C_toolsBbbPlugin;

internal static class BbbSbjdMappedDeviceService
{
    private const string CommandName = BbbPluginCommandIds.Bbb;
    private const string BlockMappingFileName = "BlockMappingConfig.json";
    private const string LegacyGoodMeProjectRootPath = @"C:\Users\WIN10\Desktop\GoodMeCadDesignPlugin";
    private static readonly string s_desktopDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    private static readonly string s_currentUserLegacyMappingPath = Path.Combine(
        s_desktopDirectory,
        "GoodMeCadDesignPlugin",
        "bin",
        "goodmePlugin",
        "config",
        BlockMappingFileName);
    private static readonly string s_legacyMappingPath = Path.Combine(
        LegacyGoodMeProjectRootPath,
        "bin",
        "goodmePlugin",
        "config",
        BlockMappingFileName);
    private static readonly string s_cadPluginsRoot = Path.Combine(s_desktopDirectory, "cadPlugins");

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly Regex s_dynamicNameSuffixRegex = new(@"-\d{12}$", RegexOptions.Compiled);
    private static readonly Regex s_shelfWidthSuffixRegex = new(@"[-－](\d{3,4})\s*$", RegexOptions.Compiled);
    private static readonly Regex s_dimensionSuffixRegex = new(@"\d{3,4}\s*[×xX*＊]\s*\d{2,4}$", RegexOptions.Compiled);

    internal static BbbSelectionCaptureResult CaptureImpliedSelectedBlocks(Document doc)
    {
        var editor = doc.Editor;
        var implied = editor.SelectImplied();
        if (implied.Status != PromptStatus.OK || implied.Value == null || implied.Value.Count == 0)
            return new BbbSelectionCaptureResult();

        return ReadSelection(doc.Database, implied.Value, "预选对象中未找到可读取的设备名称。", "预选");
    }

    internal static BbbSelectionCaptureResult CaptureSelectedBlocks(Document doc)
    {
        var impliedCapture = CaptureImpliedSelectedBlocks(doc);
        if (impliedCapture.Devices.Count > 0)
            return impliedCapture;

        var editor = doc.Editor;

        var options = new PromptSelectionOptions
        {
            MessageForAdding = "\n请选择需要读取显示设备名称并写入清单的设备块: "
        };
        var filter = new SelectionFilter(new[]
        {
            new TypedValue((int)DxfCode.Start, "INSERT")
        });
        var picked = editor.GetSelection(options, filter);
        if (picked.Status != PromptStatus.OK || picked.Value == null || picked.Value.Count == 0)
        {
            return new BbbSelectionCaptureResult
            {
                Message = picked.Status == PromptStatus.Cancel
                    ? $"{CommandName}：{UIMessages.Common.Cancelled}"
                    : UIMessages.Command.NoDeviceBlockSelected
            };
        }

        return ReadSelection(doc.Database, picked.Value, "所选对象中未找到可读取的设备名称。", "选中");
    }

    private static BbbSelectionCaptureResult ReadSelection(
        Database database,
        SelectionSet selectionSet,
        string emptyMessage,
        string sourceLabel)
    {
        if (!TryLoadMappings(out var mappings, out var loadError))
        {
            return new BbbSelectionCaptureResult
            {
                Message = loadError ?? $"{CommandName}：读取设备映射文件失败。"
            };
        }

        var devices = ReadBlocks(database, selectionSet, mappings, out var blockCount, out var mappedBlockCount, out var skippedNoMappingCount);
        if (devices.Count == 0)
        {
            return new BbbSelectionCaptureResult
            {
                Devices = devices,
                BlockCount = blockCount,
                ManagedBlockCount = mappedBlockCount,
                SkippedNoManagedNameCount = skippedNoMappingCount,
                Message = emptyMessage
            };
        }

        var message = $"已从 {mappedBlockCount} 个{sourceLabel}块读取 {devices.Count} 条设备记录。";
        if (skippedNoMappingCount > 0)
            message += $" 另有 {skippedNoMappingCount} 个块未命中设备映射规则。";

        return new BbbSelectionCaptureResult
        {
            Devices = devices,
            BlockCount = blockCount,
            ManagedBlockCount = mappedBlockCount,
            SkippedNoManagedNameCount = skippedNoMappingCount,
            Message = message
        };
    }

    private static List<BbbSelectedBlockInfo> ReadBlocks(
        Database database,
        SelectionSet selectionSet,
        IReadOnlyList<BbbSbjdBlockMapping> mappings,
        out int blockCount,
        out int mappedBlockCount,
        out int skippedNoMappingCount)
    {
        var list = new List<BbbSelectedBlockInfo>();
        blockCount = 0;
        mappedBlockCount = 0;
        skippedNoMappingCount = 0;

        var countedBlocks = 0;
        var countedMappedBlocks = 0;
        var countedSkippedBlocks = 0;
        list = CadDatabaseScope.Read(
            database,
            (_, transaction) =>
            {
                var blockInfos = new List<BbbSelectedBlockInfo>();
                foreach (SelectedObject? selectedObject in selectionSet)
                {
                    if (selectedObject?.ObjectId.IsNull != false)
                        continue;

                    if (!CadDatabaseScope.TryOpenAs<BlockReference>(transaction, selectedObject.ObjectId, OpenMode.ForRead, out var blockReference) ||
                        blockReference == null)
                    {
                        continue;
                    }

                    countedBlocks++;
                    var deviceInfos = BuildBlockInfos(blockReference, transaction, mappings);
                    if (deviceInfos.Count == 0)
                    {
                        countedSkippedBlocks++;
                        continue;
                    }

                    countedMappedBlocks++;
                    blockInfos.AddRange(deviceInfos);
                }

                return blockInfos;
            });

        blockCount = countedBlocks;
        mappedBlockCount = countedMappedBlocks;
        skippedNoMappingCount = countedSkippedBlocks;
        return list;
    }

    private static List<BbbSelectedBlockInfo> BuildBlockInfos(
        BlockReference blockReference,
        Transaction transaction,
        IReadOnlyList<BbbSbjdBlockMapping> mappings)
    {
        var realBlockName = GetEffectiveBlockName(blockReference, transaction);
        var cleanBlockName = CleanBlockName(realBlockName);
        var matchedRule = mappings.FirstOrDefault(mapping =>
            string.Equals((mapping.blockname ?? "").Trim(), cleanBlockName, StringComparison.OrdinalIgnoreCase) &&
            (!mapping.isDynamic.HasValue || mapping.isDynamic.Value == blockReference.IsDynamicBlock));
        if (matchedRule == null)
            return new List<BbbSelectedBlockInfo>();

        var mappedItems = BuildMappedItems(blockReference, transaction, matchedRule, cleanBlockName);
        if (mappedItems.Count == 0)
            return new List<BbbSelectedBlockInfo>();

        var stateDisplayText = BuildStateDisplayText(blockReference, transaction, matchedRule);
        var handleText = blockReference.Handle.ToString();
        var blockName = cleanBlockName.Length > 0 ? cleanBlockName : realBlockName;

        var result = new List<BbbSelectedBlockInfo>();
        var slotIndex = 1;
        foreach (var mappedItem in mappedItems)
        {
            for (var index = 0; index < mappedItem.Quantity; index++)
            {
                result.Add(new BbbSelectedBlockInfo
                {
                    BlockHandle = handleText,
                    BlockName = blockName,
                    StateDisplayText = stateDisplayText,
                    SlotIndex = slotIndex++,
                    DeviceName = mappedItem.DeviceName,
                    AttributePreview = mappedItem.DebugText
                });
            }
        }

        return result;
    }

    private static List<BbbSbjdMappedItem> BuildMappedItems(
        BlockReference blockReference,
        Transaction transaction,
        BbbSbjdBlockMapping matchedRule,
        string cleanBlockName)
    {
        if (string.Equals(cleanBlockName, "电视机", StringComparison.OrdinalIgnoreCase))
            return BuildTelevisionItems(blockReference);

        var mappedItems = new List<BbbSbjdMappedItem>();
        var distance1 = GetDynamicPropertyValue(blockReference, "距离1");
        var distance2 = GetDynamicPropertyValue(blockReference, "距离2");
        var leftOrRight = GetDynamicPropertyValue(blockReference, "左右");
        if (leftOrRight.Length == 0)
            leftOrRight = GetLeftOrRight(blockReference, transaction);

        var mainMaterialName = BuildMaterialName(matchedRule, distance1, distance2, leftOrRight);
        AddMappedItem(
            mappedItems,
            mainMaterialName,
            $"主件 {BuildSpecificationText(matchedRule.specifications, distance1, distance2, leftOrRight, matchedRule.isleftorright)}".Trim());

        if (matchedRule.visibilityParameter != null && !string.IsNullOrWhiteSpace(matchedRule.visibilityParameter.name))
        {
            var currentVisibilityValue = GetDynamicPropertyValue(blockReference, matchedRule.visibilityParameter.name);
            if (currentVisibilityValue.Length > 0 && matchedRule.visibilityParameter.mappings != null)
            {
                var matchedVisibility = matchedRule.visibilityParameter.mappings
                    .FirstOrDefault(x => string.Equals(x.visibilityValue, currentVisibilityValue, StringComparison.OrdinalIgnoreCase));
                if (matchedVisibility != null)
                {
                    var targetComponent = matchedVisibility.targetComponent ?? "";
                    var targetSpecification = matchedVisibility.targetSpecification ?? "";
                    ApplySbjdVisibilityOverride(
                        matchedRule,
                        blockReference,
                        ref targetComponent,
                        ref targetSpecification);

                    AddMappedItem(
                        mappedItems,
                        targetComponent,
                        $"{matchedRule.visibilityParameter.name}={currentVisibilityValue}，规格 {targetSpecification}".Trim('，', ' '));
                }
            }
        }

        if (matchedRule.additionalMaterials != null)
        {
            foreach (var extra in matchedRule.additionalMaterials)
            {
                var extraName = ReplacePlaceholders(extra.name ?? "", distance1, distance2, leftOrRight, matchedRule.isleftorright);
                var extraSpecification = ReplacePlaceholders(extra.specification ?? "", distance1, distance2, leftOrRight, matchedRule.isleftorright);
                AddMappedItem(mappedItems, extraName, $"附加件 {extraSpecification}".Trim());
            }
        }

        return mappedItems;
    }

    private static List<BbbSbjdMappedItem> BuildTelevisionItems(BlockReference blockReference)
    {
        var mappedItems = new List<BbbSbjdMappedItem>();
        var visibilityValue = GetDynamicPropertyValue(blockReference, "可见性1");
        if (visibilityValue.Length == 0)
            return mappedItems;

        var parts = visibilityValue.Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
            return mappedItems;

        var tvSize = parts[2].Trim();
        var quantity = int.TryParse(parts[3].Trim(), out var parsedQuantity) && parsedQuantity > 0
            ? parsedQuantity
            : 1;

        var materialName = tvSize switch
        {
            "43" => "AOC商业显示器43寸F5H",
            "40" => "AOC商业显示器40寸",
            _ => $"AOC商业显示器{tvSize}寸"
        };

        AddMappedItem(mappedItems, materialName, $"可见性1={visibilityValue}", quantity);
        return mappedItems;
    }

    private static void AddMappedItem(List<BbbSbjdMappedItem> mappedItems, string? deviceName, string debugText, int quantity = 1)
    {
        var normalizedName = (deviceName ?? "").Trim();
        if (normalizedName.Length == 0 || quantity <= 0)
            return;

        mappedItems.Add(new BbbSbjdMappedItem
        {
            DeviceName = normalizedName,
            Quantity = quantity,
            DebugText = debugText
        });
    }

    private static string BuildMaterialName(BbbSbjdBlockMapping rule, string distance1, string distance2, string leftOrRight)
    {
        var remark = rule.remark ?? "";
        if (ContainsIgnoreCase(remark, "直接生成"))
            return (rule.materialname ?? "").Trim();

        var result = ReplacePlaceholders(rule.materialname ?? "", distance1, distance2, leftOrRight, rule.isleftorright);

        if (string.Equals((rule.blockname ?? "").Trim(), "水吧台", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(distance2, "700", StringComparison.Ordinal))
        {
            var side = leftOrRight.Length > 0 ? leftOrRight : "左";
            return $"水吧台水池{side}（双漏板）{distance1}（非标）";
        }

        if (string.Equals((rule.blockname ?? "").Trim(), "独立水池", StringComparison.OrdinalIgnoreCase) &&
            (!string.Equals(distance1, "500", StringComparison.Ordinal) || !string.Equals(distance2, "500", StringComparison.Ordinal)))
        {
            result += "定制";
        }

        return result.Trim();
    }

    private static string BuildSpecificationText(
        string? specificationTemplate,
        string distance1,
        string distance2,
        string leftOrRight,
        string? isLeftOrRightEnabled)
    {
        return ReplacePlaceholders(specificationTemplate ?? "", distance1, distance2, leftOrRight, isLeftOrRightEnabled).Trim();
    }

    private static string ReplacePlaceholders(string template, string distance1, string distance2, string leftOrRight, string? isLeftOrRightEnabled)
    {
        var result = template;
        if (distance1.Length > 0)
            result = ReplaceOrdinal(result, "距离1", distance1);
        if (distance2.Length > 0)
            result = ReplaceOrdinal(result, "距离2", distance2);

        if (!ContainsOrdinal(result, "左右"))
            return result;

        if (string.Equals(isLeftOrRightEnabled, "是", StringComparison.OrdinalIgnoreCase))
        {
            var replacement = leftOrRight.Length > 0 ? leftOrRight : "左";
            result = ReplaceOrdinal(result, "左右", replacement);
        }

        return result;
    }

    private static string BuildStateDisplayText(
        BlockReference blockReference,
        Transaction transaction,
        BbbSbjdBlockMapping matchedRule)
    {
        var parts = new List<string>();
        var distance1 = GetDynamicPropertyValue(blockReference, "距离1");
        var distance2 = GetDynamicPropertyValue(blockReference, "距离2");
        if (distance1.Length > 0 || distance2.Length > 0)
            parts.Add($"{distance1}×{distance2}".Trim('×'));

        var leftOrRight = GetDynamicPropertyValue(blockReference, "左右");
        if (leftOrRight.Length == 0)
            leftOrRight = GetLeftOrRight(blockReference, transaction);
        if (leftOrRight.Length > 0)
            parts.Add($"左右={leftOrRight}");

        if (matchedRule.visibilityParameter != null && !string.IsNullOrWhiteSpace(matchedRule.visibilityParameter.name))
        {
            var visibilityValue = GetDynamicPropertyValue(blockReference, matchedRule.visibilityParameter.name);
            if (visibilityValue.Length > 0)
                parts.Add($"{matchedRule.visibilityParameter.name}={visibilityValue}");
        }

        return parts.Count == 0 ? "SBJD映射" : string.Join(" | ", parts);
    }

    private static string GetEffectiveBlockName(BlockReference blockReference, Transaction transaction)
    {
        try
        {
            if (blockReference.IsDynamicBlock && !blockReference.DynamicBlockTableRecord.IsNull)
            {
                if (CadDatabaseScope.TryOpenAs<BlockTableRecord>(transaction, blockReference.DynamicBlockTableRecord, OpenMode.ForRead, out var dynamicBlock) &&
                    dynamicBlock != null &&
                    !string.IsNullOrWhiteSpace(dynamicBlock.Name))
                {
                    return dynamicBlock.Name.Trim();
                }
            }
        }
        catch
        {
        }

        return (blockReference.Name ?? "").Trim();
    }

    private static string GetDynamicPropertyValue(BlockReference blockReference, string propertyName)
    {
        if (!blockReference.IsDynamicBlock || propertyName.Length == 0)
            return "";

        try
        {
            foreach (DynamicBlockReferenceProperty property in blockReference.DynamicBlockReferencePropertyCollection)
            {
                if (!string.Equals(property.PropertyName, propertyName, StringComparison.Ordinal))
                    continue;

                try
                {
                    var numberValue = Convert.ToDouble(property.Value);
                    return Math.Round(numberValue, 0).ToString();
                }
                catch
                {
                    return (property.Value?.ToString() ?? "").Trim();
                }
            }
        }
        catch
        {
        }

        return "";
    }

    private static void ApplySbjdVisibilityOverride(
        BbbSbjdBlockMapping rule,
        BlockReference blockReference,
        ref string targetComponent,
        ref string targetSpecification)
    {
        if (!string.Equals((rule.blockname ?? "").Trim(), "洛德304平冷+平冷上架组合", StringComparison.OrdinalIgnoreCase))
            return;

        if (!TryBuildFlatCoolRackSpecification(blockReference, out var specification))
            return;

        targetSpecification = specification;
        targetComponent = ReplaceTrailingSpecification(targetComponent, specification);
    }

    private static bool TryBuildFlatCoolRackSpecification(BlockReference blockReference, out string specification)
    {
        specification = "";

        var visibilityValue = GetDynamicPropertyValue(blockReference, "可见性1");
        if (visibilityValue.Length == 0)
            return false;

        var match = s_shelfWidthSuffixRegex.Match(visibilityValue);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var width) || width <= 0)
            return false;

        specification = $"{width}×400";
        return true;
    }

    private static string ReplaceTrailingSpecification(string targetComponent, string specification)
    {
        var trimmedComponent = (targetComponent ?? "").Trim();
        if (trimmedComponent.Length == 0)
            return specification;

        if (s_dimensionSuffixRegex.IsMatch(trimmedComponent))
            return s_dimensionSuffixRegex.Replace(trimmedComponent, specification);

        return trimmedComponent.IndexOf("平冷上架", StringComparison.Ordinal) >= 0
            ? $"平冷上架{specification}"
            : trimmedComponent;
    }

    private static string GetLeftOrRight(BlockReference blockReference, Transaction transaction)
    {
        foreach (ObjectId attributeId in blockReference.AttributeCollection)
        {
            if (!CadDatabaseScope.TryOpenAs<AttributeReference>(transaction, attributeId, OpenMode.ForRead, out var attributeReference) ||
                attributeReference == null)
            {
                continue;
            }

            var text = attributeReference.TextString ?? "";
            if (ContainsOrdinal(text, "右"))
                return "右";
            if (ContainsOrdinal(text, "左"))
                return "左";
        }

        return "";
    }

    private static string CleanBlockName(string blockName)
    {
        var trimmed = (blockName ?? "").Trim();
        return trimmed.Length == 0 ? "" : s_dynamicNameSuffixRegex.Replace(trimmed, "").Trim();
    }

    private static bool TryLoadMappings(out IReadOnlyList<BbbSbjdBlockMapping> mappings, out string? error)
    {
        mappings = Array.Empty<BbbSbjdBlockMapping>();
        error = null;

        if (!TryResolveBlockMappingPath(out var blockMappingPath))
        {
            error = $"{CommandName}：未找到 SBJD 映射文件。已检查：{s_currentUserLegacyMappingPath}；{s_legacyMappingPath}；{Path.Combine(s_cadPluginsRoot, "goodmePlugin-*", "config", BlockMappingFileName)}";
            return false;
        }

        try
        {
            var json = File.ReadAllText(blockMappingPath);
            var loaded = JsonSerializer.Deserialize<List<BbbSbjdBlockMapping>>(json, s_jsonOptions) ?? new List<BbbSbjdBlockMapping>();
            mappings = loaded
                .Where(x => !string.IsNullOrWhiteSpace(x.blockname))
                .ToList();
            if (mappings.Count == 0)
            {
                error = $"{CommandName}：SBJD 映射文件为空：{blockMappingPath}";
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = $"{CommandName}：解析 SBJD 映射文件失败：{ex.Message}";
            return false;
        }
        catch (IOException ex)
        {
            error = $"{CommandName}：读取 SBJD 映射文件失败：{ex.Message}";
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = $"{CommandName}：读取 SBJD 映射文件失败：{ex.Message}";
            return false;
        }
    }

    private static bool TryResolveBlockMappingPath(out string mappingPath)
    {
        foreach (var candidate in EnumerateDirectMappingPathCandidates())
        {
            if (File.Exists(candidate))
            {
                mappingPath = candidate;
                return true;
            }
        }

        if (Directory.Exists(s_cadPluginsRoot))
        {
            var latestVersionPath = Directory
                .EnumerateDirectories(s_cadPluginsRoot, "goodmePlugin-*", SearchOption.TopDirectoryOnly)
                .Select(dir => Path.Combine(dir, "config", BlockMappingFileName))
                .Where(File.Exists)
                .OrderByDescending(path => new FileInfo(path).LastWriteTimeUtc)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(latestVersionPath))
            {
                mappingPath = latestVersionPath;
                return true;
            }
        }

        mappingPath = "";
        return false;
    }

    private static IEnumerable<string> EnumerateDirectMappingPathCandidates()
    {
        yield return s_currentUserLegacyMappingPath;
        yield return s_legacyMappingPath;
    }

    private static bool ContainsIgnoreCase(string? text, string value)
    {
        return !string.IsNullOrEmpty(text) &&
               text!.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool ContainsOrdinal(string text, string value)
    {
        return text.IndexOf(value, StringComparison.Ordinal) >= 0;
    }

    private static string ReplaceOrdinal(string text, string oldValue, string newValue)
    {
        return ContainsOrdinal(text, oldValue)
            ? text.Replace(oldValue, newValue)
            : text;
    }
}

internal sealed class BbbSbjdBlockMapping
{
    public string blockname { get; set; } = "";
    public string blocktype { get; set; } = "";
    public string isleftorright { get; set; } = "";
    public string specifications { get; set; } = "";
    public string remark { get; set; } = "";
    public string materialname { get; set; } = "";
    public bool? isDynamic { get; set; }
    public BbbSbjdVisibilityParameter? visibilityParameter { get; set; }
    public List<BbbSbjdAdditionalMaterial>? additionalMaterials { get; set; }
}

internal sealed class BbbSbjdVisibilityParameter
{
    public string name { get; set; } = "";
    public List<BbbSbjdVisibilityMapping>? mappings { get; set; }
}

internal sealed class BbbSbjdVisibilityMapping
{
    public string visibilityValue { get; set; } = "";
    public string targetComponent { get; set; } = "";
    public string targetSpecification { get; set; } = "";
}

internal sealed class BbbSbjdAdditionalMaterial
{
    public string name { get; set; } = "";
    public string specification { get; set; } = "";
}

internal sealed class BbbSbjdMappedItem
{
    internal string DeviceName { get; set; } = "";
    internal int Quantity { get; set; } = 1;
    internal string DebugText { get; set; } = "";
}
