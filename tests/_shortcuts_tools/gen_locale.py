# -*- coding: utf-8 -*-
"""Generate Shortcuts plugin Locale.resx / Locale.zh-Hans.resx / Locale.Designer.cs from one table."""
import os
from xml.sax.saxutils import escape

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")
OUT = os.path.join(ROOT, "src", "Widgets", "Shortcuts", "Locales")

# (key, english, chinese)
ENTRIES = [
    # ---- registration -------------------------------------------------------
    ("Shortcuts", "Shortcuts", "快捷指令"),
    ("Shortcuts_Title", "Shortcuts", "快捷指令"),
    ("Shortcuts_Subtitle", "Compose multi-step workflows and run them with one tap", "把多步操作串成一条指令，点按即可运行"),
    ("Shortcuts_NewName", "New Shortcut", "新指令"),
    ("Shortcuts_Unit_Percent", "{0}%", "{0}%"),
    ("Shortcuts_Unit_Times", "{0} times", "{0} 次"),
    ("Shortcuts_Param_CopyOf", "{0} copy", "{0} 副本"),
    ("Shortcuts_Error_Timeout", "Timed out", "执行超时"),
    ("Shortcuts_Settings_Open", "Open", "打开"),

    # ---- card ---------------------------------------------------------------
    ("Shortcuts_Tile_New", "New Shortcut", "新建指令"),
    ("Shortcuts_Tile_TapToRun", "Tap to run", "点按运行"),
    ("Shortcuts_Tile_Running", "Running", "运行中"),
    ("Shortcuts_Tile_Step", "Step {0}/{1}", "第 {0}/{1} 步"),
    ("Shortcuts_Tile_Done", "Done", "已完成"),
    ("Shortcuts_Tile_Failed", "Failed", "失败"),
    ("Shortcuts_Tile_Cancelled", "Cancelled", "已取消"),
    ("Shortcuts_Card_Empty", "No shortcuts yet", "还没有指令"),
    ("Shortcuts_Card_EmptyHint", "Tap to create your first shortcut", "点按创建第一条指令"),

    # ---- context menu / common actions -------------------------------------
    ("Shortcuts_Menu_Run", "Run", "运行"),
    ("Shortcuts_Menu_Edit", "Edit", "编辑"),
    ("Shortcuts_Menu_Duplicate", "Duplicate", "复制一份"),
    ("Shortcuts_Menu_Delete", "Delete", "删除"),
    ("Shortcuts_Menu_New", "New Shortcut", "新建指令"),
    ("Shortcuts_Menu_Stop", "Stop", "停止"),
    ("Shortcuts_Delete_Message", "Delete \"{0}\"? This cannot be undone.", "删除「{0}」？此操作不可撤销。"),
    ("Shortcuts_Delete_Ok", "Delete", "删除"),
    ("Shortcuts_Cancel", "Cancel", "取消"),

    # ---- popup panel --------------------------------------------------------
    ("Shortcuts_Popup_Title", "Shortcuts", "快捷指令"),
    ("Shortcuts_Popup_Subtitle", "{0} shortcuts", "{0} 个指令"),
    ("Shortcuts_Add", "New", "新建"),
    ("Shortcuts_Search_Placeholder", "Search shortcuts", "搜索指令"),
    ("Shortcuts_Empty", "No shortcuts yet", "还没有指令"),
    ("Shortcuts_EmptyHint", "Start from a template or a blank shortcut", "从模板或空白开始创建"),
    ("Shortcuts_Template_Header", "Start from a template", "从模板开始"),
    ("Shortcuts_Template_Blank", "Blank Shortcut", "空白指令"),
    ("Shortcuts_Template_Lock", "Lock Screen", "一键锁屏"),
    ("Shortcuts_Template_Mute", "Quiet Focus", "静音专注"),
    ("Shortcuts_Template_Shot", "Screenshot Archive", "截屏归档"),
    ("Shortcuts_Actions_Count", "{0} actions", "{0} 个动作"),
    ("Shortcuts_Actions_CountOne", "1 action", "1 个动作"),
    ("Shortcuts_NoMatch", "No matching shortcuts", "没有匹配的指令"),

    # ---- editor chrome ------------------------------------------------------
    ("Shortcuts_Editor_Title", "Shortcut Editor", "快捷指令编辑器"),
    ("Shortcuts_Done", "Done", "完成"),
    ("Shortcuts_Undo", "Undo", "撤销"),
    ("Shortcuts_Redo", "Redo", "重做"),
    ("Shortcuts_Grip", "Drag to reorder", "拖拽排序"),
    ("Shortcuts_Editor_NameWatermark", "Shortcut name", "指令名称"),
    ("Shortcuts_IconPicker_Title", "Icon & Color", "图标与颜色"),
    ("Shortcuts_NoActions", "No actions yet - add one below", "还没有动作，从下方添加"),
    ("Shortcuts_Add_Action", "Add Action", "添加动作"),
    ("Shortcuts_Library_Search", "Search actions", "搜索动作"),
    ("Shortcuts_Library_Recent", "Recently used", "最近使用"),
    ("Shortcuts_Action_Delete", "Delete Action", "删除动作"),
    ("Shortcuts_Action_Duplicate", "Duplicate Action", "复制动作"),
    ("Shortcuts_Action_MoveUp", "Move Up", "上移"),
    ("Shortcuts_Action_MoveDown", "Move Down", "下移"),
    ("Shortcuts_InvalidValue", "Invalid value", "值无效"),

    # ---- control-flow markers ----------------------------------------------
    ("Shortcuts_EndRepeat", "End Repeat", "结束重复"),
    ("Shortcuts_EndIf", "End If", "结束如果"),
    ("Shortcuts_Else", "Otherwise", "否则"),
    ("Shortcuts_Collapse", "Collapse", "折叠"),
    ("Shortcuts_Expand", "Expand", "展开"),

    # ---- parameter editor ---------------------------------------------------
    ("Shortcuts_Param_Missing", "Not set", "未设置"),
    ("Shortcuts_Param_Value", "Value", "值"),
    ("Shortcuts_Param_Variable", "Variable", "变量"),
    ("Shortcuts_Param_UseValue", "Value", "固定值"),
    ("Shortcuts_Param_UseVariable", "Insert variable", "插入变量"),
    ("Shortcuts_Param_LastResult", "Result of previous step", "上一步的结果"),
    ("Shortcuts_Param_ResultOf", "Result of \"{0}\"", "「{0}」的结果"),
    ("Shortcuts_Param_NoCandidates", "No previous step to reference", "还没有可引用的前置动作"),
    ("Shortcuts_Param_Browse", "Browse", "浏览"),
    ("Shortcuts_Param_PickFile", "Pick a file", "选择文件"),
    ("Shortcuts_Param_PickFolder", "Pick a folder", "选择文件夹"),
    ("Shortcuts_Param_PickApp", "Pick an application", "选择应用"),
    ("Shortcuts_Param_SecondsUnit", "{0} s", "{0} 秒"),

    # ---- parameter labels ---------------------------------------------------
    ("Shortcuts_Param_target", "App", "应用"),
    ("Shortcuts_Param_args", "Arguments", "启动参数"),
    ("Shortcuts_Param_path", "Path", "路径"),
    ("Shortcuts_Param_url", "URL", "网址"),
    ("Shortcuts_Param_query", "Query", "搜索内容"),
    ("Shortcuts_Param_engine", "Engine", "搜索引擎"),
    ("Shortcuts_Param_level", "Volume", "音量"),
    ("Shortcuts_Param_seconds", "Wait", "等待时长"),
    ("Shortcuts_Param_text", "Text", "文本"),
    ("Shortcuts_Param_message", "Message", "内容"),
    ("Shortcuts_Param_format", "Format", "格式"),
    ("Shortcuts_Param_min", "Minimum", "最小值"),
    ("Shortcuts_Param_max", "Maximum", "最大值"),
    ("Shortcuts_Param_left", "Value", "左值"),
    ("Shortcuts_Param_op", "Condition", "条件"),
    ("Shortcuts_Param_right", "Compared to", "比较值"),
    ("Shortcuts_Param_count", "Times", "次数"),
    ("Shortcuts_Param_input", "Input", "输入"),
    ("Shortcuts_Param_operation", "Operation", "方式"),
    ("Shortcuts_Param_find", "Find", "查找"),
    ("Shortcuts_Param_replace_with", "Replace with", "替换为"),
    ("Shortcuts_Param_note", "Note", "注释"),
    ("Shortcuts_Param_mode", "Action", "动作"),
    ("Shortcuts_Param_surface", "Material", "材质"),
    ("Shortcuts_Param_title", "Reminder", "提醒内容"),

    # ---- choice values ------------------------------------------------------
    ("Shortcuts_Choice_upper", "UPPERCASE", "转为大写"),
    ("Shortcuts_Choice_lower", "lowercase", "转为小写"),
    ("Shortcuts_Choice_trim", "Trim whitespace", "去除首尾空白"),
    ("Shortcuts_Choice_replace", "Replace text", "文本替换"),
    ("Shortcuts_Op_equals", "equals", "等于"),
    ("Shortcuts_Op_notEquals", "is not", "不等于"),
    ("Shortcuts_Op_contains", "contains", "包含"),
    ("Shortcuts_Op_startsWith", "starts with", "开头是"),
    ("Shortcuts_Op_endsWith", "ends with", "结尾是"),
    ("Shortcuts_Op_greater", "is greater than", "大于"),
    ("Shortcuts_Op_less", "is less than", "小于"),
    ("Shortcuts_Op_isEmpty", "is empty", "为空"),
    ("Shortcuts_Op_isNotEmpty", "is not empty", "不为空"),
    ("Shortcuts_Engine_bing", "Bing", "必应"),
    ("Shortcuts_Engine_baidu", "Baidu", "百度"),
    ("Shortcuts_Engine_google", "Google", "Google"),
    ("Shortcuts_Mode_hide", "Hide", "隐藏"),
    ("Shortcuts_Mode_show", "Show", "显示"),
    ("Shortcuts_Mode_toggle", "Toggle", "切换"),
    ("Shortcuts_Dark_dark", "Dark", "深色"),
    ("Shortcuts_Dark_light", "Light", "浅色"),
    ("Shortcuts_Dark_system", "Follow system", "跟随系统"),
    ("Shortcuts_Surface_cycle", "Cycle", "循环切换"),
    ("Shortcuts_Surface_acrylic", "Frosted Glass", "毛玻璃"),
    ("Shortcuts_Surface_solid", "Solid", "纯色"),
    ("Shortcuts_Surface_liquidglass", "Liquid Glass", "液态玻璃"),
    ("Shortcuts_Surface_softglow", "Soft Glow", "柔光玻璃"),
    ("Shortcuts_Surface_liquidglassv2", "Liquid Glass 2.0", "新液态玻璃"),
    ("Shortcuts_Surface_colorful", "Colorful", "多彩"),

    # ---- category names -----------------------------------------------------
    ("Shortcuts_Cat_Launch", "Launch", "打开与启动"),
    ("Shortcuts_Cat_Media", "Media", "媒体"),
    ("Shortcuts_Cat_System", "System & Tools", "系统与工具"),
    ("Shortcuts_Cat_Text", "Text & Data", "文本与数据"),
    ("Shortcuts_Cat_Flow", "Control Flow", "分支与循环"),
    ("Shortcuts_Cat_DeskCanvas", "Widget Actions", "组件联动"),

    # ---- action titles ------------------------------------------------------
    ("Shortcuts_Action_openApp", "Open App", "打开应用"),
    ("Shortcuts_Action_openFile", "Open File or Folder", "打开文件或文件夹"),
    ("Shortcuts_Action_openUrl", "Open URL", "打开网址"),
    ("Shortcuts_Action_webSearch", "Search the Web", "浏览器搜索"),
    ("Shortcuts_Action_mediaPlayPause", "Play / Pause", "播放或暂停"),
    ("Shortcuts_Action_mediaNext", "Next Track", "下一曲"),
    ("Shortcuts_Action_mediaPrevious", "Previous Track", "上一曲"),
    ("Shortcuts_Action_setVolume", "Set Volume", "设定音量"),
    ("Shortcuts_Action_muteToggle", "Toggle Mute", "切换静音"),
    ("Shortcuts_Action_lockScreen", "Lock Screen", "锁定屏幕"),
    ("Shortcuts_Action_sleep", "Sleep", "让电脑休眠"),
    ("Shortcuts_Action_screenshot", "Take Screenshot", "截取屏幕"),
    ("Shortcuts_Action_emptyRecycleBin", "Empty Recycle Bin", "清空回收站"),
    ("Shortcuts_Action_wait", "Wait", "等待"),
    ("Shortcuts_Action_notify", "Show Notification", "显示通知"),
    ("Shortcuts_Action_getClipboard", "Get Clipboard", "读取剪贴板"),
    ("Shortcuts_Action_setClipboard", "Copy to Clipboard", "复制到剪贴板"),
    ("Shortcuts_Action_textTransform", "Transform Text", "文本变换"),
    ("Shortcuts_Action_currentDate", "Current Date & Time", "当前日期时间"),
    ("Shortcuts_Action_randomNumber", "Random Number", "随机数"),
    ("Shortcuts_Action_if", "If", "如果"),
    ("Shortcuts_Action_repeat", "Repeat", "重复"),
    ("Shortcuts_Action_comment", "Comment", "注释"),
    ("Shortcuts_Action_themeSurface", "Switch Theme Material", "切换主题材质"),
    ("Shortcuts_Action_themeDarkMode", "Dark / Light Mode", "深浅色模式"),
    ("Shortcuts_Action_widgetsVisibility", "Show / Hide Widgets", "组件显隐"),
    ("Shortcuts_Action_addReminder", "Add Reminder", "添加提醒"),
    ("Shortcuts_Action_openDataFolder", "Open Data Folder", "打开数据目录"),

    # ---- run outputs --------------------------------------------------------
    ("Shortcuts_Output_Volume", "Volume {0}%", "音量 {0}%"),
    ("Shortcuts_Output_Muted", "Muted", "已静音"),
    ("Shortcuts_Output_Unmuted", "Unmuted", "已取消静音"),
    ("Shortcuts_Output_Locked", "Screen locked", "屏幕已锁定"),
    ("Shortcuts_Output_Sleeping", "Going to sleep", "即将休眠"),
    ("Shortcuts_Output_Screenshot", "Screenshot saved", "截图已保存"),
    ("Shortcuts_Output_BinEmptied", "Recycle bin emptied", "回收站已清空"),
    ("Shortcuts_Output_BinAlreadyEmpty", "Recycle bin is already empty", "回收站已经是空的"),
    ("Shortcuts_Output_NotificationSent", "Notification sent", "通知已发送"),
    ("Shortcuts_Output_Random", "Random: {0}", "随机数：{0}"),
    ("Shortcuts_Output_NoMedia", "No media is playing", "没有正在播放的媒体"),
    ("Shortcuts_Output_Copied", "Copied", "已复制"),
    ("Shortcuts_Output_ClipboardEmpty", "Clipboard is empty", "剪贴板为空"),
    ("Shortcuts_Output_Theme", "Switched to {0}", "已切换到{0}"),
    ("Shortcuts_Output_WidgetsHidden", "Desktop widgets hidden", "已隐藏桌面组件"),
    ("Shortcuts_Output_WidgetsShown", "Desktop widgets shown", "已显示桌面组件"),
    ("Shortcuts_Output_ReminderAdded", "Reminder added: {0}", "已添加提醒：{0}"),
    ("Shortcuts_Output_DataFolder", "Data folder opened", "已打开数据目录"),

    # ---- errors -------------------------------------------------------------
    ("Shortcuts_Error_EmptyParam", "Please fill in \"{0}\"", "请先填写「{0}」"),
    ("Shortcuts_Error_Failed", "Failed: {0}", "执行失败：{0}"),
    ("Shortcuts_Error_NoApp", "Cannot start \"{0}\"", "无法启动「{0}」"),
    ("Shortcuts_Error_UnknownAction", "Unknown action: {0}", "未知动作：{0}"),
    ("Shortcuts_Error_NoService", "Not available in this environment", "当前环境不支持该操作"),
    ("Shortcuts_Error_Notification", "Cannot show a notification", "无法显示系统通知"),
    ("Shortcuts_Error_ConfirmReject", "Cancelled by user", "已取消"),
    ("Shortcuts_ConfirmDestructive", "This action deletes data and cannot be undone. Continue?", "该操作会删除数据且不可恢复，继续执行吗？"),
    ("Shortcuts_ConfirmRunMessage", "Run \"{0}\"?", "运行「{0}」吗？"),
    ("Shortcuts_ConfirmRunOk", "Run", "运行"),

    # ---- settings page ------------------------------------------------------
    ("Shortcuts_Settings_MaxTiles", "Tiles on card", "卡片显示上限"),
    ("Shortcuts_Settings_Confirm", "Confirm before running", "运行前确认"),
    ("Shortcuts_Settings_Edit", "Edit Shortcut", "编辑指令"),
    ("Shortcuts_Settings_OpenEditor", "Open Editor", "打开编辑器"),
    ("Shortcuts_Settings_OpenPanel", "Open Panel", "打开面板"),
    ("Shortcuts_Settings_Hint", "Compose actions in the editor; tap a tile on the card to run the shortcut.", "在编辑器里编排动作，卡片上点按瓷贴即可运行。"),
    ("Shortcuts_Switch", "Switch Shortcut", "切换指令"),
]


def resx(entries, include_zh=False):
    header = '''<?xml version="1.0" encoding="utf-8"?>
<root>
    <xsd:schema id="root" xmlns="" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:msdata="urn:schemas-microsoft-com:xml-msdata">
        <xsd:element name="root" msdata:IsDataSet="true" />
    </xsd:schema>
    <resheader name="resmimetype">
        <value>text/microsoft-resx</value>
    </resheader>
    <resheader name="version">
        <value>1.3</value>
    </resheader>
    <resheader name="reader">
        <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=2.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
    </resheader>
    <resheader name="writer">
        <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=2.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
    </resheader>
'''
    body = ""
    for key, en, zh in entries:
        value = zh if include_zh else en
        body += '    <data name="%s" xml:space="preserve">\n        <value>%s</value>\n    </data>\n' % (key, escape(value))
    return header + body + '</root>\n'


def designer(entries):
    def cs_escape(v):
        return v.replace('\\', '\\\\').replace('"', '\\"')

    lines = []
    lines.append('//------------------------------------------------------------------------------')
    lines.append('// <auto-generated>')
    lines.append('//     This code was generated by a tool (Shortcuts locale table).')
    lines.append('//     Changes to this file may cause incorrect behavior and will be lost if')
    lines.append('//     the code is regenerated.')
    lines.append('// </auto-generated>')
    lines.append('//------------------------------------------------------------------------------')
    lines.append('')
    lines.append('namespace Shortcuts.Locales {')
    lines.append('    using System;')
    lines.append('    ')
    lines.append('    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("System.Resources.Tools.StronglyTypedResourceBuilder", "4.0.0.0")]')
    lines.append('    [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]')
    lines.append('    [global::System.Runtime.CompilerServices.CompilerGeneratedAttribute()]')
    lines.append('    public class Locale {')
    lines.append('        ')
    lines.append('        private static global::System.Resources.ResourceManager resourceMan;')
    lines.append('        ')
    lines.append('        private static global::System.Globalization.CultureInfo resourceCulture;')
    lines.append('        ')
    lines.append('        [global::System.Diagnostics.CodeAnalysis.SuppressMessageAttribute("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode")]')
    lines.append('        internal Locale() {')
    lines.append('        }')
    lines.append('        ')
    lines.append('        [global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Advanced)]')
    lines.append('        public static global::System.Resources.ResourceManager ResourceManager {')
    lines.append('            get {')
    lines.append('                if (object.ReferenceEquals(resourceMan, null)) {')
    lines.append('                    global::System.Resources.ResourceManager temp = new global::System.Resources.ResourceManager("Shortcuts.Locales.Locale", typeof(Locale).Assembly);')
    lines.append('                    resourceMan = temp;')
    lines.append('                }')
    lines.append('                return resourceMan;')
    lines.append('            }')
    lines.append('        }')
    lines.append('        ')
    lines.append('        [global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Advanced)]')
    lines.append('        public static global::System.Globalization.CultureInfo Culture {')
    lines.append('            get {')
    lines.append('                return resourceCulture;')
    lines.append('            }')
    lines.append('            set {')
    lines.append('                resourceCulture = value;')
    lines.append('            }')
    lines.append('        }')
    lines.append('        ')
    for key, en, zh in entries:
        lines.append('        public static string %s => ResourceManager.GetString("%s", resourceCulture) ?? "%s";' % (key, key, cs_escape(en)))
    lines.append('    }')
    lines.append('}')
    lines.append('')
    return "\n".join(lines)


os.makedirs(OUT, exist_ok=True)
with open(os.path.join(OUT, "Locale.resx"), "w", encoding="utf-8") as f:
    f.write(resx(ENTRIES, include_zh=False))
with open(os.path.join(OUT, "Locale.zh-Hans.resx"), "w", encoding="utf-8") as f:
    f.write(resx(ENTRIES, include_zh=True))
with open(os.path.join(OUT, "Locale.Designer.cs"), "w", encoding="utf-8") as f:
    f.write(designer(ENTRIES))
print("Generated %d entries -> %s" % (len(ENTRIES), OUT))
