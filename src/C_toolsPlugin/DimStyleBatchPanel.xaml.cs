using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsPlugin;

public partial class DimStyleBatchPanel : UserControl
{
    private readonly ObservableCollection<DimStyleGroupInfo> _groups = new();
    private readonly ObservableCollection<ArrowBlockListItem> _arrowBlocks = new();
    private readonly ObservableCollection<string> _textStyles = new();
    private readonly ObservableCollection<DimStyleFieldRow> _fieldRows = new();
    private bool _comboReady;

    /// <summary>状态栏回调（与系统配置窗共用）。</summary>
    public Action<string>? SetStatusCallback { get; set; }

    public DimStyleBatchPanel()
    {
        InitializeComponent();
        DataContext = this;
        DimGroupCombo.ItemsSource = _groups;
        foreach (var r in CreateFieldRows())
            _fieldRows.Add(r);
    }

    /// <summary>供 XAML 绑定标注字段列表。</summary>
    public ObservableCollection<DimStyleFieldRow> FieldRows => _fieldRows;

    /// <summary>供箭头下拉绑定（与快捷键列表区 ComboBox 一致用深色模板）。</summary>
    public ObservableCollection<ArrowBlockListItem> ArrowBlocks => _arrowBlocks;

    /// <summary>供文字样式（DIMTXSTY）下拉绑定，来自当前图纸文字样式表。</summary>
    public ObservableCollection<string> TextStyles => _textStyles;

    private static IEnumerable<DimStyleFieldRow> CreateFieldRows()
    {
        yield return new DimStyleFieldRow("arrow", "箭头块名", "DIMBLK：空为默认闭合箭头，或从当前图纸块表选择");
        yield return new DimStyleFieldRow("dimasz", "箭头大小", "DIMASZ");
        yield return new DimStyleFieldRow("dimclrd", "尺寸线颜色", "DIMCLRD：BYLAYER / BYBLOCK / 索引 0～256");
        yield return new DimStyleFieldRow("dimexe", "界线超出尺寸线", "DIMEXE：尺寸界线超出尺寸线的距离");
        yield return new DimStyleFieldRow("dimfxlenon", "固定长度界线", "DIMFXLENON：是否使用固定长度的尺寸界线");
        yield return new DimStyleFieldRow("dimfxlen", "固定长度值", "DIMFXL：尺寸界线固定长度（须同时勾选「固定长度界线」）");
        yield return new DimStyleFieldRow("dimtxsty", "文字样式", "DIMTXSTY：从当前图纸文字样式表下拉选择");
        yield return new DimStyleFieldRow("dimclrt", "文字颜色", "DIMCLRT");
        yield return new DimStyleFieldRow("dimtxt", "文字高度", "DIMTXT");
        yield return new DimStyleFieldRow("dimrnd", "舍入值", "DIMRND");
    }

    private DimStyleFieldRow Row(string key)
    {
        foreach (var r in _fieldRows)
        {
            if (string.Equals(r.RowKey, key, StringComparison.Ordinal))
                return r;
        }

        throw new InvalidOperationException("未知行键：" + key);
    }

    private void SetStatus(string s) => SetStatusCallback?.Invoke(s);

    internal void RefreshFromDocument()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            SetStatus("无活动图纸，无法读取标注样式。");
            _groups.Clear();
            _arrowBlocks.Clear();
            _textStyles.Clear();
            return;
        }

        try
        {
            var batchData = CadDatabaseScope.Read(
                doc,
                (db, tr) => (
                    Groups: DimStyleBatchService.ListGroups(tr, db),
                    ArrowNames: DimStyleBatchService.ListArrowBlockNames(tr, db),
                    TextStyleNames: DimStyleBatchService.ListTextStyleNames(tr, db)),
                requireDocumentLock: true);

            _arrowBlocks.Clear();
            _arrowBlocks.Add(new ArrowBlockListItem(""));
            foreach (var n in batchData.ArrowNames)
                _arrowBlocks.Add(new ArrowBlockListItem(n));

            _textStyles.Clear();
            foreach (var n in batchData.TextStyleNames)
                _textStyles.Add(n);

            _comboReady = false;
            _groups.Clear();
            foreach (var g in batchData.Groups)
                _groups.Add(g);

            var (select, preferredSampleStyleName) = ResolvePreferredGroupSelection();
            DimGroupCombo.SelectedItem = select;
            _comboReady = true;

            SetStatus("已刷新标注样式分组（" + _groups.Count + " 组）。");

            if (select != null)
                LoadGroupSample(select, preferredSampleStyleName);
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("刷新标注样式分组", ex);
            SetStatus("刷新失败：" + ex.Message);
        }
    }

    private void DimGroupCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_comboReady)
            return;
        if (DimGroupCombo.SelectedItem is not DimStyleGroupInfo g)
            return;

        DimStyleLastGroupStore.Save(g.Prefix);
        LoadGroupSample(g);
    }

    private (DimStyleGroupInfo? Group, string? PreferredSampleStyleName) ResolvePreferredGroupSelection()
    {
        var configuredStyleName = UserConfigurationStore.TryGetDimStyleName();
        if (!string.IsNullOrWhiteSpace(configuredStyleName))
        {
            foreach (var group in _groups)
            {
                var match = FindStyleNameMatch(group.StyleNames, configuredStyleName);
                if (match != null)
                    return (group, match);
            }
        }

        var configuredPrefix = UserConfigurationStore.TryGetDimStyleGroupPrefix();
        if (!string.IsNullOrWhiteSpace(configuredPrefix))
        {
            foreach (var group in _groups)
            {
                if (string.Equals(group.Prefix, configuredPrefix, StringComparison.OrdinalIgnoreCase))
                    return (group, null);
            }
        }

        var saved = DimStyleLastGroupStore.TryGetPrefix();
        if (saved is { Length: > 0 })
        {
            foreach (var group in _groups)
            {
                if (string.Equals(group.Prefix, saved, StringComparison.OrdinalIgnoreCase))
                    return (group, null);
            }
        }

        return _groups.Count > 0 ? (_groups[0], null) : (null, null);
    }

    private void LoadGroupSample(DimStyleGroupInfo group, string? preferredSampleStyleName = null)
    {
        if (group.StyleNames.Count == 0)
            return;

        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;

        var sampleStyleName = FindStyleNameMatch(group.StyleNames, preferredSampleStyleName) ?? group.StyleNames[0];
        try
        {
            var sampleResult = CadDatabaseScope.Read(
                doc,
                (db, tr) =>
                {
                    var ok = DimStyleBatchService.TryReadSample(tr, db, sampleStyleName, out var state, out var err);
                    return (Success: ok, State: state, Error: err);
                },
                requireDocumentLock: true);

            if (sampleResult.Success)
            {
                ApplyStateToUi(sampleResult.State);
            }
            else
            {
                SetStatus(sampleResult.Error ?? "读取样本失败");
            }
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取标注样式样本", ex);
            SetStatus("读取失败：" + ex.Message);
        }
    }

    private static string? FindStyleNameMatch(IReadOnlyList<string> styleNames, string? preferredStyleName)
    {
        var target = preferredStyleName?.Trim() ?? "";
        if (target.Length == 0)
            return null;

        foreach (var styleName in styleNames)
        {
            if (string.Equals(styleName, target, StringComparison.OrdinalIgnoreCase))
                return styleName;
        }

        return null;
    }

    private void ApplyStateToUi(DimStyleBatchFormState state)
    {
        EnsureArrowSelection(state.ArrowBlockName);
        Row("dimasz").TextValue = state.Dimasz;
        Row("dimclrd").TextValue = state.Dimclrd;
        Row("dimexe").TextValue = state.Dimexe;
        Row("dimfxlenon").BoolValue = state.DimfxlenOn;
        Row("dimfxlen").TextValue = state.Dimfxlen;
        EnsureTextStyleSelection(state.TextStyleName);
        Row("dimclrt").TextValue = state.Dimclrt;
        Row("dimtxt").TextValue = state.Dimtxt;
        Row("dimrnd").TextValue = state.Dimrnd;
    }

    private void EnsureArrowSelection(string? blockName)
    {
        var want = blockName?.Trim() ?? "";
        ArrowBlockListItem? found = null;
        foreach (var x in _arrowBlocks)
        {
            if (string.Equals(x.BlockName, want, StringComparison.OrdinalIgnoreCase))
            {
                found = x;
                break;
            }
        }

        if (found == null && want.Length > 0)
        {
            found = new ArrowBlockListItem(want);
            _arrowBlocks.Add(found);
        }

        Row("arrow").ArrowBlockName = found?.BlockName ?? _arrowBlocks.FirstOrDefault()?.BlockName ?? "";
    }

    private void EnsureTextStyleSelection(string? styleName)
    {
        var want = styleName?.Trim() ?? "";
        if (want.Length == 0)
        {
            Row("dimtxsty").TextValue = "";
            return;
        }

        string? match = null;
        foreach (var s in _textStyles)
        {
            if (string.Equals(s, want, StringComparison.OrdinalIgnoreCase))
            {
                match = s;
                break;
            }
        }

        if (match == null)
        {
            _textStyles.Add(want);
            match = want;
        }

        Row("dimtxsty").TextValue = match;
    }

    private static DimStyleBatchFormState ReadStateFromUi(
        string arrow,
        string dimasz,
        string dimclrd,
        string dimexe,
        bool dimfxlenOn,
        string dimfxlen,
        string textStyle,
        string dimclrt,
        string dimtxt,
        string dimrnd)
    {
        return new DimStyleBatchFormState
        {
            ArrowBlockName = arrow,
            Dimasz = dimasz,
            Dimclrd = dimclrd,
            Dimexe = dimexe,
            DimfxlenOn = dimfxlenOn,
            Dimfxlen = dimfxlen,
            TextStyleName = textStyle,
            Dimclrt = dimclrt,
            Dimtxt = dimtxt,
            Dimrnd = dimrnd
        };
    }

    /// <summary>将当前表单值写入本组全部标注样式（原「应用到本组」；现由系统配置窗顶部「保存」在标注样式页触发）。</summary>
    internal void ApplyToCurrentGroup(Action<bool, string>? completed = null)
    {
        if (DimGroupCombo.SelectedItem is not DimStyleGroupInfo g)
        {
            const string message = "请先选择分组。";
            SetStatus(message);
            completed?.Invoke(false, message);
            return;
        }

        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            const string message = "无活动图纸。";
            SetStatus(message);
            completed?.Invoke(false, message);
            return;
        }

        var values = ReadStateFromUi(
            Row("arrow").ArrowBlockName.Trim(),
            Row("dimasz").TextValue.Trim(),
            Row("dimclrd").TextValue.Trim(),
            Row("dimexe").TextValue.Trim(),
            Row("dimfxlenon").BoolValue,
            Row("dimfxlen").TextValue.Trim(),
            Row("dimtxsty").TextValue.Trim(),
            Row("dimclrt").TextValue.Trim(),
            Row("dimtxt").TextValue.Trim(),
            Row("dimrnd").TextValue.Trim());

        try
        {
            AcAp.DocumentManager.ExecuteInApplicationContext(
                _ =>
                {
                    try
                    {
                        string? applyError = null;
                        var applied = CadDatabaseScope.Write(
                            doc,
                            (db, tr) =>
                            {
                                var ok = DimStyleBatchService.TryApply(tr, db, g.StyleNames, values, out var err);
                                applyError = err;
                                return ok;
                            },
                            requireDocumentLock: true);

                        if (!applied)
                        {
                            var failMessage = applyError ?? "应用失败";
                            Dispatcher.BeginInvoke(() =>
                            {
                                SetStatus(failMessage);
                                completed?.Invoke(false, failMessage);
                            });
                            return;
                        }

                        // 写入 DimStyle 表后须重生成，否则屏幕上的尺寸/标注外观可能仍显示旧样式
                        try
                        {
                            doc.Editor.Regen();
                        }
                        catch (System.Exception ex)
                        {
                            C_toolsDiagnostics.LogNonFatal("应用到本组后 Regen", ex);
                        }

                        var successMessage = "已写入本组 " + g.StyleNames.Count + " 个标注样式（前缀 " + g.Prefix + "）；已重生成当前视图。";
                        Dispatcher.BeginInvoke(() =>
                        {
                            SetStatus(successMessage);
                            completed?.Invoke(true, successMessage);
                        });
                    }
                    catch (System.Exception ex)
                    {
                        C_toolsDiagnostics.LogNonFatal("批量写标注样式", ex);
                        var failMessage = "写入失败：" + ex.Message;
                        Dispatcher.BeginInvoke(() =>
                        {
                            SetStatus(failMessage);
                            completed?.Invoke(false, failMessage);
                        });
                    }
                },
                null);
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("ExecuteInApplicationContext（标注样式）", ex);
            var failMessage = "无法切换到 CAD 上下文：" + ex.Message;
            SetStatus(failMessage);
            completed?.Invoke(false, failMessage);
        }
    }
}

/// <summary>箭头块下拉项：空块名为默认闭合箭头。</summary>
public sealed class ArrowBlockListItem
{
    public string BlockName { get; }

    public ArrowBlockListItem(string blockName) => BlockName = blockName ?? "";

    public string Display => string.IsNullOrEmpty(BlockName) ? "（默认闭合箭头）" : BlockName;

    public override string ToString() => Display;
}
