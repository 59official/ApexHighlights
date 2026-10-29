#!/usr/bin/env python3
"""从 Inno Setup 自带的 Default.isl 生成简体中文语言文件。

官方发行包不带中文（Languages\ 里 29 种语言，没有 zh-CN），而安装界面全英文对
中文用户太劝退。这里不手工维护整份 .isl —— 那样 Inno 升级后新增的键会悄悄缺
失、编译报错 —— 而是拿官方 Default.isl 当模板，只覆盖我们真正会看到的那几十条，
剩下的保留英文（出问题时至少能读懂）。

用法（路径指向 Inno Setup 安装目录里的 Default.isl）：
    python3 packaging/make-zh-isl.py [Default.isl 路径] [输出路径]
"""

import io
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_SRC = os.path.join(HERE, "..", ".tools", "innosetup", "Default.isl")
DEFAULT_OUT = os.path.join(HERE, "Languages", "ChineseSimplified.isl")

# 只翻「用户真会看见」的：标题、按钮、欢迎/目录/任务/安装中/完成、卸载、报错提示。
# 没列的（磁盘、共享文件、签名校验之类）保留英文。
ZH = {
    # 标题
    "SetupAppTitle": "安装程序",
    "SetupWindowTitle": "安装 - %1",
    "UninstallAppTitle": "卸载",
    "UninstallAppFullTitle": "卸载 %1",
    "InformationTitle": "信息",
    "ConfirmTitle": "确认",
    "ErrorTitle": "错误",
    "ConfirmUninstall": "确定要卸载 %1 吗？%n%n录好的片段不会被删掉。",

    # 按钮
    "ButtonBack": "< 上一步(&B)",
    "ButtonNext": "下一步(&N) >",
    "ButtonInstall": "安装(&I)",
    "ButtonOK": "确定",
    "ButtonCancel": "取消",
    "ButtonYes": "是(&Y)",
    "ButtonYesToAll": "全是(&A)",
    "ButtonNo": "否(&N)",
    "ButtonNoToAll": "全否(&O)",
    "ButtonFinish": "完成(&F)",
    "ButtonBrowse": "浏览(&B)...",
    "ButtonWizardBrowse": "浏览(&R)...",
    "ButtonNewFolder": "新建文件夹(&M)",

    # 启动 / 退出
    "SetupLdrStartupMessage": "即将安装 %1。要继续吗？",
    "ExitSetupTitle": "退出安装",
    "ExitSetupMessage": "安装还没完成。现在退出的话，程序不会被装上。%n%n要退出安装程序吗？",
    "ClickNext": "点「下一步」继续，或点「取消」退出安装。",
    "CannotContinue": "安装没法继续了，点「取消」退出。",
    "LastErrorMessage": "%1。%n%n错误 %2：%3",

    # 欢迎
    "WelcomeLabel1": "欢迎使用 [name] 安装向导",
    "WelcomeLabel2": "即将在你的电脑上安装 [name/ver]。%n%n建议先关掉正在跑的游戏和录制窗口，再点「下一步」。",

    # 选目录
    "WizardSelectDir": "选择安装位置",
    "SelectDirDesc": "要把 [name] 装到哪个文件夹？",
    "SelectDirLabel3": "安装程序会把 [name] 装进下面这个文件夹。",
    "SelectDirBrowseLabel": "点「下一步」继续；想换地方就点「浏览」。",
    "DiskSpaceMBLabel": "至少需要 [mb] MB 可用磁盘空间。",
    "DiskSpaceGBLabel": "至少需要 [gb] GB 可用磁盘空间。",

    # 选任务 / 开始菜单
    "WizardSelectTasks": "选择附加任务",
    "SelectTasksDesc": "安装 [name] 时还要顺手做哪些事？",
    "SelectTasksLabel2": "选好附带任务后点「下一步」。",
    "WizardSelectProgramGroup": "选择开始菜单文件夹",
    "SelectStartMenuFolderDesc": "要把快捷方式放到开始菜单的哪个文件夹？",
    "SelectStartMenuFolderLabel3": "安装程序会在下面这个开始菜单文件夹里创建快捷方式。",
    "SelectStartMenuFolderBrowseLabel": "点「下一步」继续；想换地方就点「浏览」。",
    "NoProgramGroupCheck2": "不创建开始菜单文件夹(&S)",

    # 准备 / 安装中
    "WizardReady": "准备安装",
    "ReadyLabel1": "安装程序已就绪，即将开始安装 [name]。",
    "ReadyLabel2a": "点「安装」开始；想再检查设置就点「上一步」。",
    "ReadyLabel2b": "点「安装」开始安装。",
    "WizardPreparing": "正在准备",
    "PreparingDesc": "正在准备安装 %1，请稍候…",
    "WizardInstalling": "正在安装",
    "InstallingLabel": "正在安装 [name]，请稍候…",
    "StatusClosingApplications": "正在关闭占用文件的程序…",
    "StatusCreateDirs": "正在创建文件夹…",
    "StatusExtractFiles": "正在解压文件…",
    "StatusCreateIcons": "正在创建快捷方式…",
    "StatusCreateRegistryEntries": "正在写入注册表…",
    "StatusSavingUninstall": "正在写入卸载信息…",

    # 完成
    "FinishedHeadingLabel": "[name] 装好了",
    "FinishedLabel": "安装完成。开始菜单里已经有 [name] 的快捷方式，双击就能启动。",
    "FinishedLabelNoIcons": "安装完成。",
    "ClickFinish": "点「完成」退出安装程序。",
    "RunEntryExec": "运行 %1",
    "RunEntryShellExec": "用 %1 打开",

    # 卸载
    "UninstallStatusLabel": "正在删除 %1，请稍候…",
    "StatusUninstalling": "正在卸载 %1…",
    "UninstalledAll": "%1 已经卸载干净。",
    "UninstalledMost": "%1 已卸载，但有少量文件没删掉，可以手动处理。",
    "UninstalledAndNeedsRestart": "要彻底卸掉 %1 得重启电脑。%n%n现在就重启吗？",

    # 图标 / 程序组
    "AdditionalIcons": "附加图标：",
    "CreateDesktopIcon": "创建桌面快捷方式(&D)",
    "UninstallProgram": "卸载 %1",
    "LaunchProgram": "启动 %1",
    "AutoStartProgram": "开机时自动启动 %1(&A)",
    "NameAndVersion": "%1 版本 %2",

    # 正在跑的程序挡路
    "ApplicationsFound": "下面这些程序正占用着要替换的文件，得先关掉才能继续：%n%n%1",
    "ApplicationsFound2": "下面这些程序正占用着要替换的文件。要安装程序自动把它们关掉吗？%n%n%1",
    "CloseApplications": "自动关掉（推荐）",
    "DontCloseApplications": "不关，我自己关",
    "PrivilegesRequiredOverrideTitle": "选择安装方式",
    "PrivilegesRequiredOverrideInstruction": "请选择安装方式。",
    "PrivilegesRequiredOverrideText1": "%1 只装给你一个人，不需要管理员权限。",
    "PrivilegesRequiredOverrideText2": "%1 装给这台电脑上的所有人，需要管理员权限。",
    "PrivilegesRequiredOverrideAllUsers": "为所有人安装(&A)",
    "PrivilegesRequiredOverrideAllUsersRecommended": "为所有人安装(&A)（推荐）",
    "PrivilegesRequiredOverrideCurrentUser": "仅为我安装(&M)",
    "PrivilegesRequiredOverrideCurrentUserRecommended": "仅为我安装(&M)（推荐）",
    "SetupAppRunningError": "检测到 %1 正在运行。%n%n请先在托盘图标上右键退出，再点「确定」继续；或者点「取消」退出安装。",
    "UninstallAppRunningError": "%1 正在运行，请先在托盘图标上右键退出，再点「确定」。",
    "ShutdownBlockReasonInstallingApp": "正在安装 %1。",
    "ShutdownBlockReasonUninstallingApp": "正在卸载 %1。",

    # 目录 / 磁盘之类的小提示
    "DirExistsTitle": "文件夹已存在",
    "DirExists": "文件夹：%n%n%1%n%n已经存在，装进去吗？",
    "DirDoesntExistTitle": "文件夹不存在",
    "DirDoesntExist": "文件夹：%n%n%1%n%n不存在，要新建吗？",
    "DiskSpaceWarningTitle": "磁盘空间不足",
    "DiskSpaceWarning": "安装至少需要 %1 KB，目前只剩 %2 KB。%n%n仍要继续吗？",
    "ErrorCreatingDir": "安装程序没法创建文件夹「%1」。",
    "BrowseDialogTitle": "选择文件夹",
    "BrowseDialogLabel": "在下面挑一个文件夹，然后点「确定」。",
    "NewFolderName": "新建文件夹",
}

HEADER = """; 简体中文语言文件（ApexHighlights 打包用）
;
; 这份文件由 packaging/make-zh-isl.py 从 Inno Setup 自带的 Default.isl 生成：
; 只覆盖用户会看到的那些条目，其余保留英文。要改文案就改那个脚本再重新生成，
; 别直接手改这里 —— 下次生成会被覆盖，而且 Inno 升级后新增的键会漏。
"""

LANG_OPTIONS = """[LangOptions]
LanguageName=简体中文
LanguageID=$0804
LanguageCodePage=936
DialogFontName=Microsoft YaHei UI
DialogFontSize=9
DialogFontStandardHeight=15
"""


def load_messages(path):
    """把 Default.isl 读成 {段名: (有序键列表, 键->值)}，只收 [Messages] 与 [CustomMessages]。"""
    order, values = [], {}
    sections = {}
    in_messages = False
    pending = None
    with io.open(path, encoding="utf-8-sig", errors="replace") as f:
        for raw in f:
            line = raw.rstrip("\r\n")
            stripped = line.strip()
            if stripped.startswith("[") and stripped.endswith("]"):
                name = stripped.strip("[]").lower()
                in_messages = name in ("messages", "custommessages")
                if in_messages:
                    order, values = [], {}          # 段一换就换一组，别把两段混在一起
                    sections[name] = (order, values)
                    pending = None
                continue
            if not in_messages or not stripped or stripped.startswith(";"):
                continue
            m = re.match(r"^([A-Za-z0-9]+)\s*=(.*)$", stripped)
            if m:
                key, value = m.group(1), m.group(2)
                order.append(key)
                pending = key
                values[key] = value
            elif pending is not None:
                # 续行：isl 允许值折行（下一行接着写）
                values[pending] += "\n" + stripped
    return sections


def main():
    src = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_SRC
    out = sys.argv[2] if len(sys.argv) > 2 else DEFAULT_OUT
    if not os.path.exists(src):
        sys.exit("找不到 Default.isl：%s（传路径或先装 Inno Setup）" % src)

    sections = load_messages(src)
    if "messages" not in sections:
        sys.exit("Default.isl 里没读到 [Messages] 段，文件结构是不是变了？")
    order, values = sections["messages"]
    cm_order, cm_values = sections.get("custommessages", ([], {}))
    missing = [k for k in ZH if k not in values]
    if missing:
        print("注意：这些键在模板里没有，已跳过 -> " + ", ".join(sorted(missing)))

    os.makedirs(os.path.dirname(out), exist_ok=True)
    with io.open(out, "w", encoding="utf-8-sig", newline="\r\n") as f:
        f.write(HEADER)
        f.write("\n")
        f.write(LANG_OPTIONS)
        f.write("\n[Messages]\n")
        for key in order:
            f.write("%s=%s\n" % (key, ZH.get(key, values[key])))

        # [CustomMessages]：快捷方式/任务说明、卸载项名称、进度条文字都在这一段
        if cm_order:
            f.write("\n[CustomMessages]\n")
            for key in cm_order:
                f.write("%s=%s\n" % (key, ZH.get(key, cm_values[key])))

    translated = sum(1 for k in list(order) + list(cm_order) if k in ZH)
    total = len(order) + len(cm_order)
    print("已写出 %s：%d 条（其中自定义 %d 条），中文 %d 条"
          % (out, total, len(cm_order), translated))


if __name__ == "__main__":
    main()
