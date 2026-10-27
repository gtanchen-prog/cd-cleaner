# C盘清理助手 (CdCleaner)

一个轻量的 Windows 缓存清理工具：WinForms 图形界面、深色/浅色主题、清理明细日志、每周定时自动清理。

只清理**缓存与临时文件**，不会碰系统文件、页面文件、休眠文件、系统还原点，也不会删除文档/桌面/下载等个人资料。

## 功能特性

- **13 类清理目标**：系统/用户临时文件、Windows 更新缓存、Prefetch、错误报告（WER）、内存转储、浏览器缓存、缩略图/图标缓存、DirectX 着色器缓存、崩溃转储、应用缓存（LocalCache）、各盘根目录 Temp
- **浏览器覆盖**：Chrome / Edge / Brave / Chromium / Vivaldi / Opera / Firefox，包含全部用户配置（管理员模式下含其他用户）
- **按盘选择**：驱动器勾选影响回收站与各盘 Temp
- **回收站单独确认**：显示文件数与体积，必须勾选"不可恢复"才能清空
- **主题**：跟随系统 / 浅色 / 深色，选择会记住；深色标题栏在最小化到托盘再恢复后保持
- **托盘**：最小化驻留托盘，双击恢复
- **定时清理**：一键创建 Windows 计划任务（每周指定时间静默清理缓存）
- **结果汇总**：清理结束弹窗显示总释放量、各项明细与用时
- **安全护栏**：路径白名单，拒绝盘符根目录、个人资料目录、System32/WinSxS/Installer 等

## 系统要求

- Windows 10 / 11（自带 .NET Framework 4.8）
- 无需安装，绿色单文件

## 使用

1. **[⬇ 下载最新版 CdCleaner.exe](https://github.com/gtanchen-prog/cd-cleaner/releases/latest)**（约 62 KB）
2. 双击运行，UAC 提示点"是"获得完整清理权限
3. 勾选清理项目 → 扫描大小 → 开始清理

详细说明见 [使用说明.txt](使用说明.txt)。

## 目录结构

```
使用说明.txt         用户手册
启动清理助手.bat     启动器（纯 ASCII）
build.bat           一键重新编译
README.md           本文件
src/CdCleaner.cs    主程序源码（C# 5 / WinForms）
src/app.manifest    应用清单（DPI 感知、asInvoker）
src/app.ico         图标
src/CdCleaner_v1.0.ps1  旧版 PowerShell 脚本（备份）
```

## 重新编译

仓库不提交 exe，克隆后双击 `build.bat` 即可用系统自带 .NET Framework 编译器生成 `CdCleaner.exe`：

```
csc /target:winexe /optimize+ /codepage:65001 /win32icon:src\app.ico /win32manifest:src\app.manifest /out:CdCleaner.exe src\CdCleaner.cs
```

## 清理范围与安全说明

| 会清理 | 不会清理 |
|---|---|
| Temp、浏览器缓存（Cache/Code Cache/GPUCache/Service Worker） | Cookies、登录状态、LocalStorage |
| Prefetch、更新下载缓存、WER、转储 | 书签、历史记录、保存的密码 |
| 缩略图/图标缓存、D3D 缓存、应用缓存 | 文档/桌面/下载/图片等个人资料 |
| 各盘 Temp、回收站（需单独确认） | 系统文件、页面文件、休眠文件、系统还原点、WinSxS |

## 常见问题

**Windows 提示 "Windows 已保护你的电脑"（SmartScreen）？**

程序未做代码签名，首次运行会被 SmartScreen 拦截：点「更多信息」→「仍要运行」即可。
这是所有个人开发程序的常态，不是文件本身有问题。如需校验，Release 附件 `CdCleaner.exe` 约 62 KB 且提供 SHA-256 摘要。

**杀毒软件报毒？**

缓存清理类工具容易被启发式规则误报。可把程序目录加入白名单，或直接下载源码用 `build.bat` 自行编译（编译产物与发布包一致）。

**标题栏显示"受限模式"？**

没有拿到管理员权限：退出后右键程序 →「以管理员身份运行」，或在 UAC 弹窗中选「是」。受限模式下系统缓存、回收站等项目会被跳过。

**定时任务没有执行？**

计划任务默认"仅在用户登录时运行"，到点需要开机且已登录。可在「任务计划程序 → CdCleanerWeeklyClean → 上次运行结果」查看执行状态。

**清理后网站需要重新登录吗？**

不会。程序只删除缓存目录（Cache / Code Cache / GPUCache / Service Worker 缓存），不触碰 Cookies、LocalStorage、书签和保存的密码。

## 许可证

未指定。如需开源建议补充 MIT / Apache-2.0 等许可证文件。
