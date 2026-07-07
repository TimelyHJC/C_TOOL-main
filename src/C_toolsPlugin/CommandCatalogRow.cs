using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace C_toolsPlugin;

/// <summary>
/// 命令表行：命令别名→PGP；图层别名→JSON + c_tools_layer_shortcuts.lsp（A1 等直接命令）。
/// </summary>
public sealed class CommandCatalogRow : INotifyPropertyChanged
{
    private string _commandName = "";
    private string _aliasesSummary = "";
    private string _source = "";
    private string _categoryTag = "";
    private string _layerName = "";
    private string _layerColor = "";
    private string _layerLinetype = "";
    private string _layerLineWeight = "";
    private bool _layerRunDimensionWhenNoSelection;
    private string _layerHatchStyleJson = "";
    private string _alias = "";
    private bool _aliasIsSuggestedDefault;
    private string _description = "";
    private bool _isUserModified;

    public CommandCatalogRow()
    {
    }

    /// <param name="categoryTag">CAD原生命令 / V命令 / 插件命令 / 图层命令（图层行由「添加图层快捷」创建）</param>
    public CommandCatalogRow(string commandName, string aliasesSummary, string source, string categoryTag = "—")
    {
        _commandName = commandName;
        _aliasesSummary = aliasesSummary;
        _source = source;
        _categoryTag = categoryTag;
    }

    /// <summary>分类：与标签页标题一致。</summary>
    public string CategoryTag
    {
        get => _categoryTag;
        set
        {
            if (_categoryTag == value) return;
            _categoryTag = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LayerCommandDisplay));
        }
    }

    /// <summary>全局命令名；图层行内部仍存占位「（别名即命令）」供合并逻辑识别；<see cref="LayerCommandDisplay"/> 为只读展示文本（界面已不单独列出命令列）。</summary>
    public string CommandName
    {
        get => _commandName;
        set
        {
            if (_commandName == value) return;
            _commandName = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LayerCommandDisplay));
        }
    }

    /// <summary>图层行在命令行中的代号与「图层快捷键」列一致（供绑定或将来扩展）；未填快捷键时返回说明占位。</summary>
    public string LayerCommandDisplay =>
        !string.Equals(_categoryTag, CadCommandCatalogBuilder.TagLayerShortcut, StringComparison.Ordinal)
            ? _commandName
            : string.IsNullOrWhiteSpace(_alias)
                ? PluginCommandIds.LayerShortcutCatalogCommandLabel
                : _alias.Trim();

    /// <summary>PGP 中已有别名摘要（扫描用，可不显示在表头）。</summary>
    public string AliasesSummary
    {
        get => _aliasesSummary;
        set
        {
            if (_aliasesSummary == value) return;
            _aliasesSummary = value;
            OnPropertyChanged();
        }
    }

    public string Source
    {
        get => _source;
        set
        {
            if (_source == value) return;
            _source = value;
            OnPropertyChanged();
        }
    }

    /// <summary>仅图层命令：目标图层名。</summary>
    public string LayerName
    {
        get => _layerName;
        set
        {
            if (_layerName == value) return;
            _layerName = value;
            OnPropertyChanged();
        }
    }

    /// <summary>仅图层命令：颜色 ACI 1–255，空则新建图层时用 CAD 默认。</summary>
    public string LayerColor
    {
        get => _layerColor;
        set
        {
            if (_layerColor == value) return;
            _layerColor = value;
            OnPropertyChanged();
        }
    }

    /// <summary>仅图层命令：线型名（如 Continuous）。</summary>
    public string LayerLinetype
    {
        get => _layerLinetype;
        set
        {
            if (_layerLinetype == value) return;
            _layerLinetype = value;
            OnPropertyChanged();
        }
    }

    /// <summary>仅图层命令：线宽，0 或空表示默认；也可填枚举名。</summary>
    public string LayerLineWeight
    {
        get => _layerLineWeight;
        set
        {
            if (_layerLineWeight == value) return;
            _layerLineWeight = value;
            OnPropertyChanged();
        }
    }

    /// <summary>仅图层命令：无预选切层后是否自动启动对齐标注（<c>DIMALIGNED</c>）；已拾取填充样式时不可用。</summary>
    public bool LayerRunDimensionWhenNoSelection
    {
        get => _layerRunDimensionWhenNoSelection;
        set
        {
            if (_layerRunDimensionWhenNoSelection == value) return;
            _layerRunDimensionWhenNoSelection = value;
            OnPropertyChanged();
        }
    }

    /// <summary>仅图层：已拾取填充样式时禁用「尺寸标注」（与填充快捷键互斥）。</summary>
    public bool LayerDimensionCheckboxEnabled => string.IsNullOrWhiteSpace(_layerHatchStyleJson);

    /// <summary>仅图层：拾取的填充样式 JSON（<see cref="HatchStyleSnapshot"/>）。</summary>
    public string LayerHatchStyleJson
    {
        get => _layerHatchStyleJson;
        set
        {
            var v = value ?? "";
            if (_layerHatchStyleJson == v) return;
            _layerHatchStyleJson = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LayerHatchStyleDisplay));
            OnPropertyChanged(nameof(LayerHatchPicked));
            OnPropertyChanged(nameof(LayerDimensionCheckboxEnabled));
        }
    }

    /// <summary>仅图层：是否已拾取填充样式（界面用于显示「已拾取」）。</summary>
    public bool LayerHatchPicked => !string.IsNullOrWhiteSpace(_layerHatchStyleJson);

    /// <summary>仅图层：填充样式摘要（界面只读）。</summary>
    public string LayerHatchStyleDisplay
    {
        get
        {
            var s = HatchStyleSnapshot.TryParseJson(_layerHatchStyleJson);
            return s == null ? "（未拾取）" : s.FormatDisplay();
        }
    }

    /// <summary>简称/代号；图层行仅存 JSON；其它页写入 C_TOOL PGP 块。</summary>
    public string Alias
    {
        get => _alias;
        set => SetAliasFromUser(value);
    }

    /// <summary>保存命令别名时使用的当前显示值；默认补全的别名也应随“保存”写入 PGP，使界面显示即生效。</summary>
    internal string AliasForCommandSave => _alias;

    internal IEnumerable<string> EnumerateAliasTokensForCommandSave()
    {
        var target = CadPgpMerge.NormalizeTarget(_commandName);
        foreach (var a in CadPgpMerge.EnumerateNormalizedAliasTokensFromCell(_alias))
        {
            if (target.Length > 0 && string.Equals(a, target, StringComparison.OrdinalIgnoreCase))
                continue;
            yield return a;
        }
    }

    /// <summary>保存到命令目录快照时使用的别名单元格；默认补全的展示别名不写入快照，刷新时仍按默认规则重新计算。</summary>
    internal string AliasForPersistence => _aliasIsSuggestedDefault ? "" : _alias;

    internal bool AliasIsDefault => _aliasIsSuggestedDefault;

    /// <summary>当前别名是否仅为界面展示的默认建议值，保存时不应覆盖用户现有 PGP 设置。</summary>
    internal bool AliasIsSuggestedDefault => _aliasIsSuggestedDefault;

    /// <summary>从用户现有 PGP / 快照回填显式别名。</summary>
    internal void SetAliasFromCatalog(string? alias) => SetAliasCore(alias, aliasIsSuggestedDefault: false);

    /// <summary>从用户现有 PGP / 快照回填显式别名。</summary>
    internal void SetExplicitAliasFromCatalog(string? alias) => SetAliasCore(alias, aliasIsSuggestedDefault: false);

    /// <summary>仅用于界面兜底展示默认别名，不代表用户已保存到 PGP。</summary>
    internal void SetAliasFromDefault(string? alias) => SetAliasCore(alias, aliasIsSuggestedDefault: !string.IsNullOrWhiteSpace(alias));

    /// <summary>仅用于界面兜底展示默认别名，不代表用户已保存到 PGP。</summary>
    internal void SetSuggestedDefaultAlias(string? alias) => SetAliasCore(alias, aliasIsSuggestedDefault: !string.IsNullOrWhiteSpace(alias));

    /// <summary>用户在网格中确认编辑了别名列后，将默认建议值提升为显式别名。</summary>
    internal void MarkAliasAsExplicit()
    {
        if (!_aliasIsSuggestedDefault)
            return;
        _aliasIsSuggestedDefault = false;
        OnPropertyChanged(nameof(AliasForPersistence));
        OnPropertyChanged(nameof(AliasIsDefault));
        OnPropertyChanged(nameof(AliasIsSuggestedDefault));
    }

    /// <summary>说明（自定义备注）。</summary>
    public string Description
    {
        get => _description;
        set
        {
            if (_description == value) return;
            _description = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 本次打开窗口后用户是否编辑过该行。保存成功弹窗的摘要仅列出此类行；点「刷新」会重载列表，新行对象上为 false。
    /// </summary>
    public bool IsUserModified
    {
        get => _isUserModified;
        set
        {
            if (_isUserModified == value) return;
            _isUserModified = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetAliasFromUser(string? value)
    {
        var v = value ?? "";
        if (_alias == v)
            return;
        SetAliasCore(v, aliasIsSuggestedDefault: false);
    }

    private void SetAliasCore(string? value, bool aliasIsSuggestedDefault)
    {
        var v = value ?? "";
        if (_alias == v && _aliasIsSuggestedDefault == aliasIsSuggestedDefault)
            return;

        var aliasChanged = _alias != v;
        var defaultChanged = _aliasIsSuggestedDefault != aliasIsSuggestedDefault;
        _alias = v;
        _aliasIsSuggestedDefault = aliasIsSuggestedDefault;

        if (aliasChanged)
        {
            OnPropertyChanged(nameof(Alias));
            OnPropertyChanged(nameof(LayerCommandDisplay));
        }

        if (aliasChanged || defaultChanged)
        {
            OnPropertyChanged(nameof(AliasForPersistence));
            OnPropertyChanged(nameof(AliasIsDefault));
            OnPropertyChanged(nameof(AliasIsSuggestedDefault));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
