using System.Collections.Generic;
using System.IO;

namespace C_toolsPlugin;

/// <summary>
/// 系统配置表默认行：内置与历史 <c>配置.ini</c> 等价的文本，不再从磁盘读取 <c>配置.ini</c> 或 <c>*.arg</c>。
/// </summary>
internal static class SysConfigBuiltInDefaults
{
    internal static List<SysConfigRow> GetDefaultRows() => ParseIniLines(EmbeddedIniText);

    private static List<SysConfigRow> ParseIniLines(string text)
    {
        var list = new List<SysConfigRow>();
        using var sr = new StringReader(text);
        string? raw;
        while ((raw = sr.ReadLine()) != null)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal))
                continue;

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var name = line[..eq].Trim();
            if (name.Length == 0)
                continue;

            var tail = line[(eq + 1)..].Trim();
            string value;
            string comment;
            var semi = tail.IndexOf(';');
            if (semi >= 0)
            {
                value = tail[..semi].Trim();
                comment = tail[(semi + 1)..].Trim();
            }
            else
            {
                value = tail;
                comment = "";
            }

            list.Add(new SysConfigRow(name, value, comment));
        }

        return list;
    }

    private const string EmbeddedIniText =
        """
        CURSORSIZE = 100	;按屏幕大小百分比确定光标的大小(默认为5.建议100)
        PICKBOX = 7	;拾取框大小(建议7)
        ZOOMFACTOR = 60	;滚动增量(建议60)
        APERTURE = 7	;捕捉靶框大小
        MBUTTONPAN = 1	;1=鼠标中键平移.0=由菜单定义
        UCSICON = 0	;0=不显示UCS图标1=左下角显示3=原点处显示
        HPASSOC = 0	;填充与边界:0=不关联.1=关联
        LUNITS = 2	;线性单位:1=科学.2=小数.3=工程.4=建筑
        INSUNITS = 4	;插入单位:1=英寸2=英尺3=英里4=毫米5=厘米6=米
        SHORTCUTMENU = 2	;0=禁用所有快捷菜单.1=启用默认菜单.2=为右键确定.7/11为右键菜单
        DYNMODE = 1	;动态输入:0=关闭:1=指针2=标注3=指针+标注(Q可以不同距离偏移)
        DYNPROMPT = 1	;动态提示:0=关闭.1=打开
        PICKSTYLE = 1	;0=不使用编组和关联填充1=使用编组选择.2=使用关联填充选择.3=使用编组选择和关联填充选择
        SELECTIONPREVIEW = 0	;选择预览:0=关闭.1=激活命令时开.2=提示选对象时开
        CLASSICKEYS = 0	;按Ctrl+C可取消正在运行的命令
        COPYMODE = 0	;0=设置自动重复的COPY命令.1=设置创建单个副本的COPY命令
        HIGHLIGHT = 1	;选定对象亮显:1=打开.0=关闭
        PICKADD = 1	;选对象:1=每次都添加到其中.0=只选一次
        PICKFIRST = 1	;1=可以先选择再启动命令.0=启动命令后才能选择对象
        QAFLAGS = 0	;0=Ctrl+C先执行再选物体(1=双击图块不能自动块名)
        TRAYICONS = 0	;状态托盘:0=不显示.1=显示
        BLIPMODE = 0	;标记点:0=不显示.1=显示
        MIRRTEXT = 0	;镜像文字:0=保持方向.1=镜像
        PICKAUTO = 1	;框选:1=允许.0=关闭
        APBOX = 0	;捕捉靶框:0=关闭.1=显示
        REGENMODE = 1	;重生成:0=禁止.1=自动
        TEXTFILL = 1	;显示文字:1=填充.0=以轮廓线
        PELLIPSE = 0	;0=生成的椭圆为多段线.1=创建真正的椭圆
        FILLMODE = 1	;实体:1=显示填充.0=不填充
        UCSFOLLOW = 0	;0=更改UCS不影响当前视图.1=转换到另一个UCS时生成平面视图
        FILEDIA = 1	;打开文件对话框:1=显示.0=不显示
        STARTUP = 0	;启动时:0=自动打开样板.1=显示对话框.2=显示“开始”选项卡
        XEDIT = 1	;当前图形被参照时在位编辑:1=可以.0=禁止
        MEASUREINIT = 1	;新创建的填充和线型:1=公制.0=英制
        MEASUREMENT = 1	;当前图形填充和线型:1=公制.0=英制
        LWDISPLAY = 0	;线宽:0=不显示.1=显示
        PLINEWID = 0	;默认的多段线宽度
        TRIMMODE = 1	;为倒角和圆角修剪选定边
        AUNITS = 0	;设定角度单位:0=十进制度数.1=度/分/秒
        MAXACTVP = 64	;布局中可同时激活的视口的最大数目
        PREVIEWEFFECT = 0	;预览对象选择的效果0=虚线1=加重线2=虚线和加重线
        VTENABLE = 0	;平滑视图:0=关闭.1=打开
        GRIDMODE = 0	;栅格状态:0=关闭.1=打开
        TRACKPATH = 0	;追踪路径的显示:0=极轴+捕捉.1=极轴+对齐点与光标
        DRAWORDERCTL = 3	;次序:0=关闭.1=打开.2=打开继承.3=完全显示
        VTFPS = 1	;平滑视图缩放速度
        ACADLSPASDOC = 1	;将acad.lsp加载到:1=每一个打开的图形.0=第一个图形
        SELECTIONAREA = 0	;选择区域显示效果:0=关闭.1=打开
        HPISLANDDETECTION = 1	;1=仅填充孤岛外部.0=孤岛内的孤岛.2=边界内的所有对象
        LAYOUTREGENCTL = 1	;0=每次切换选项卡都重生成.1=“模型”和上一个布局切换时.2=第一次切时重生成
        EDGEMODE = 0	;0=TRIM和EXTEND命令不带延伸线的选定边.1=选定的对象延伸或修剪到剪切边或边界的延伸线
        ATTMODE = 2	;2=使所有属性都可见.0=属性可见关闭.1=常规
        VISRETAIN = 1	;1=外部参照图层与当前图形的图层表一起保存0=不保存
        REMEMBERFOLDERS = 0	;0=图标特性中指定了起始路径.1=对话框中使用过的最后路径
        XCLIPFRAME = 1	;参照剪裁边界:0=不可见.1=可见
        GRIPOBJLIMIT = 15000	;选择集包括的对象多于指定数量时,不显示夹点
        THUMBSAVE = 0	;0=缩略图预览不保存在图形中.1=保存在图形中
        LOCKUI = 0	;0=不锁定工具栏、面板和窗口.15=锁定所有
        OFFSETGAPTYPE = 0	;0=偏移多段线将线延伸到投影交点.1=在投影交点处圆角.2在其投影交点处进行倒角
        MENUBAR = 1	;菜单栏:1=显示.0=不显示
        QPMODE = 0	;快捷特性状态:0=禁用.1=启用
        UCSDETECT = 0	;动态UCS:0=禁用.1=激活
        ROLLOVERTIPS = 0	;光标悬停提示:0=不显示.1=显示
        HPQUICKPREVIEW = 1	;填充快速预览:0=关闭.1=开启
        HPORIGINMODE = 5	;填充预览原点:0=HPORIGIN.1=左下.2=右下.3=右上.4=左上.5=中心
        TRANSPARENCYDISPLAY = 0	;对象透明度:0=禁用.1=可见
        NAVBARDISPLAY = 0	;视口中导航栏:0=不显示.1=显示
        MIRRHATCH = 0	;镜像填充图案:0=保持图案方向.1=镜像
        SELECTIONCYCLING = 0	;重叠选择循环:0=禁用.1=显示标记
        SELECTIONEFFECT = 1	;1=启用硬件加速时将亮显光晕线.0=虚线
        PSLTSCALE = 0	;缩放时使用图纸空间单位:0=取消.1=使用
        INPUTSEARCHOPTIONFLAGS = 1	;命令搜索:0=取消.开启:1=自动完成.2=自动更正.3=自动完成+自动更正.300=全部打开
        TASKBAR = 0	;多个图形在任务栏上:0=编组显示.1=单独显示
        NAVVCUBEDISPLAY = 0	;控制ViewCube的显示:0=不显示.1=三维显示二维不显示.2=二维显示三维不显示.3=都显示
        AppStatusBarUseIcons = 0	;状态栏显示:0=中文.1=图标
        ANNOMONITOR = 0	;注释监视器:0=关闭.1=打开
        PROXYNOTICE = 0	;代理对话框:1=显示.0=不显示
        PROXYSHOW = 0	;代理对象:0=不显示.1=显示
        SHORTCUTMENUDURATION = 100	;右键显示快捷菜单时间
        TREEMAX = 1000000	;重生成图形时占用的内存
        UPDATETHUMBNAIL = 8	;0=不更新预览.1=更新模型空间预览.2=更新布局视图预览.4=更新布局预览.8=更新缩略图
        LUPREC = 0	;长度精度小数位数
        DEFAULTGIZMO = 3	;三维小控件:3=不显示.0~2=显示
        ANNOALLVISIBLE = 0	;注释:0=不显示.1=显示
        SAVEFIDELITY = 0	;注释性逼真度
        SNAPMODE = 0	;视口捕捉:0=关闭.1=打开
        COMMANDPREVIEW = 0	;显示命令的预览:0=关闭.1=打开
        WHIPTHREAD = 3	;使用额外的处理器提高速度
        ISAVEPERCENT = 0	;0=图形每一次都完全保存
        HPMAXLINES = 1000000	;填充线的最大数
        DRAGMODE = 2	;拖动对象:0=不显示轮廓.2=显示轮廓
        DBLCLKEDIT = 1	;1=启用双击编辑
        OSNAPHATCH = 0	;填充捕捉:0=关闭.1=打开
        HPDRAWORDER = 1	;填充:1=后置.3=置于边界后
        INPUTSEARCHDELAY = 100	;显示命令行列表之前延迟的毫秒数
        STARTMODE = 1	;“开始”选项卡的显示:1=启用0=关闭
        HPGAPTOL = 50	;填充时小于此间隙都被视为封闭
        HPINHERIT = 0	;填充原点取自HPORIGIN
        TEMPOVERRIDES = 1	;0=关闭.1=打开临时替代键
        CMDECHO = 1	;命令行回显:1=开.0=关
        EXPERT = 0	;专家模式:0~5(数值越大提示越少)
        LTSCALE = 1	;全局线型比例因子
        MSLTSCALE = 1	;模型空间线型按注释缩放:0=关.1=开
        CELTSCALE = 1	;新建对象所用线型比例
        ANGBASE = 0	;角度0°基准方向(度)
        ANGDIR = 0	;角度正方向:0=逆时针.1=顺时针
        AUPREC = 0	;角度显示小数位数
        UNITMODE = 0	;坐标等按当前单位格式显示:0=关.1=开
        ELEVATION = 0	;当前标高(Z)
        THICKNESS = 0	;当前厚度(拉伸)
        ISOLINES = 4	;曲面上显示素线条数
        FACETRES = 0.5	;着色/渲染网格精细度(建议0.5~10)
        FIELDEVAL = 31	;字段更新(位标志,默认31)
        QTEXTMODE = 0	;快显文字:0=关.1=开(仅显示框)
        CECOLOR = BYLAYER	;新建对象颜色(如BYLAYER/1~255)
        CELWEIGHT = -1	;新建对象线宽:-1=ByLayer.-3=默认
        CELTYPE = BYLAYER	;新建对象线型(名称)
        ORTHOMODE = 0	;正交:0=关.1=开
        POLARANG = 90	;极轴角增量(度)
        SNAPANG = 0	;捕捉角度(弧度,常用0)
        LIMCHECK = 0	;界限检查:0=关.1=开
        DSP_MODEL2D_BG = 33,40,48	;界面颜色-二维模型空间-背景（R,G,B 0～255；对应选项→显示→颜色→二维模型空间→背景）
        DSP_UNIFIED_BG = 33,40,48	;界面颜色-二维模型空间-统一背景（R,G,B；若本版无独立项会尝试兼容属性）
        DSP_LAYOUT_BG = 255,255,255	;界面颜色-图纸/布局-背景（布局与视口外图纸区域）
        DSP_BLOCKEDIT_BG = 33,40,48	;界面颜色-块编辑器-背景
        """;
}
