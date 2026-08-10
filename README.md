# Tile10

Tile10 是一个面向 Windows 11 的全屏开始屏幕，用来还原 Windows 10 桌面模式下“使用全屏开始屏幕”的体验。它覆盖当前显示器的工作区并保留任务栏，不会修改 Explorer 或替换系统文件；退出 Tile10 后，Windows 键会立即恢复系统原本的行为。

## 直接使用

1. 运行 `dist/Tile10-win-x64/Tile10.exe`。
2. 单独按一下 Windows 键显示或隐藏磁贴页。
3. `Win + D`、`Win + E` 等系统组合键仍按原方式工作。
4. 按 `Esc` 关闭磁贴页；从左下角电源菜单可以退出 Tile10。

第一次运行会扫描当前用户、所有用户的“开始菜单”快捷方式以及 `shell:AppsFolder` 中的应用，并生成一套初始磁贴。布局保存在 `%LOCALAPPDATA%\Tile10\layout.json`。

## 已实现的功能

- Windows 键全局唤出/隐藏；全屏覆盖当前鼠标所在显示器，同时保留桌面任务栏
- “通透玻璃”视觉体系：柔和景深、半透明分层、细边框与一致的现代配色
- 硬件加速的淡入、位移缩放、磁贴依次进入和 FLIP 布局回流动画
- “已固定的磁贴”“所有应用”和即时搜索三种开始视图
- 自动发现 Win32 快捷方式和 `shell:AppsFolder` 应用并提取图标
- 从“所有应用”固定新磁贴，双击或按 Enter 启动
- 按住磁贴任意位置即可拖动，支持实时重排、跨分组移动和布局回流动画
- 每个分组默认使用 4 列磁贴网格；拖动分组标题右上角手柄，可把分组调整为 2–12 列、4–32 行，相邻分组会自动让位并保存宽高
- 可把磁贴放到分组内任意空白行列；扩宽后的新增列也能直接接收磁贴，空位不会被强制压回顶部
- 可把磁贴拖到现有分组右侧的大块空白区域；Tile10 会在对应横向槽位自动建立“新分组”，并保留中间的空白分组槽位
- 拖动磁贴右下角的斜纹手柄，可从 72 × 72 无级放大到整个分组可用范围；继续拖大会自动扩展分组，最大支持 952 × 2552 并自动保存
- 右键按 Windows 10 的方式切换小、中、宽、大四种磁贴尺寸（72 / 152 / 312 网格），并带尺寸过渡动画
- 旧版布局中的磁贴文件夹会无损展开为普通磁贴，不再创建文件夹或显示居中弹窗
- 单击分组名称即可改名
- 内置时钟动态磁贴，并保留可扩展的动态内容模型
- 所有分组、尺寸、顺序和动态磁贴状态都会自动保存
- 文件资源管理器、设置、睡眠、重启和关机入口
- 电源菜单内可选择“开机自动运行”
- 单实例运行；再次打开程序会唤出已有实例

## 开发构建

需要 .NET 8 SDK 或更高版本：

```powershell
dotnet build WinTileLauncher.slnx -c Release
```

生成免安装、包含运行时的单文件版本：

```powershell
dotnet publish WinTileLauncher/WinTileLauncher.csproj -c Release -r win-x64 --self-contained true -o dist/Tile10-win-x64 -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## 说明

- Tile10 必须在后台运行，才能接管单独按下 Windows 键的动作。
- 键盘钩子会成对处理 Win 的按下和抬起；单按 Win 后再输入字母不会残留组合键状态，真正的 `Win + 字母` 组合键仍会转交给系统。
- Windows 11 已经移除了 Windows 10 的系统 Live Tile 宿主，因此普通第三方应用不会自动恢复旧版动态内容；Tile10 当前内置时钟动态磁贴。
- 某些以管理员权限运行的全屏程序或 Windows 安全桌面可能显示在 Tile10 之上，这是 Windows 的权限边界。
