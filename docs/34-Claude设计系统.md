# Claude 风格设计系统改造任务

## 官网研究（2026-11-15）

来源：https://claude.com/ 、https://claude.com/resources/articles （原 /blog 现重定向至文章列表）。已读取官网实际 HTML/CSS 和计算样式，原始研究证据临时保存在 /tmp/claude-design-research。

官网颜色：gray-050 #FAF9F5、gray-100 #F5F4ED、gray-150 #F0EEE6、gray-200 #E8E6DC、gray-950 #141413；深色 gray-850 #1F1E1D、gray-800 #262624；Clay #D97757 / hover #C6613F。正文 15px/1.5 无衬线，编辑型标题使用常规字重衬线；控件 8–12px 圆角、卡片 12–24px 圆角、细描边，主按钮墨色/暖白反转。Clay 用于强调和焦点，避免整页高饱和填色。保留行情语义和对比度。

官网字体为专属 Anthropic Sans/Serif/Mono。应用将提供同名已安装字体优先及可随包分发的 OFL 字体回退，中文沿用系统字体；不宣称专属字体在未安装时完全一致。

## 实施任务

- [x] 单一设计变量源：双主题颜色、文字角色、间距、圆角、控件尺寸和动画；生成/共享两桌面与内置网页资源。
- [x] 统一可复用按钮（主/次/轻量/危险/图标）、输入、选择、数值、复选/开关、卡片、标签、提示和文字样式；保留键盘/焦点/禁用/错误状态。
- [x] 可复用 drawer/modal/loading，迁移现有检查器、对话框和加载状态，保留取消/关闭及减少动画。
- [x] 所有页面和嵌入图表/策略组件使用设计系统；保留紧凑行情工作区、Lucide 图标和网页功能。
- [x] 组件画廊覆盖主题/语言/常见状态，实际页面/弹窗/drawer 截图，交互、对比度、原生构建及 CLI/MCP 回归。
- [x] AGENTS.md 和维护文档规定新功能复用组件与变量，不再逐页复制控件模板和配色。

CLI/MCP：本次为桌面视觉与组件基础设施，业务操作和权限不变；现有 CLI/MCP 覆盖继续在 CI 运行。


## 维护入口与组件对照

| 组件 | Windows | macOS | 内置网页 |
| --- | --- | --- | --- |
| 设计变量/字体 | 生成 Tokens.xaml + DesignTokens.cs | 生成 Tokens.axaml/ThemeDefaults.axaml | 生成 design-tokens.css |
| 按钮/输入/select/状态 | DesignSystem/Components.xaml | DesignSystem/Components.axaml | components.css |
| 卡片/设置行 | AppCard（可选 Header/Icon） | AppCard（组合标题与内容） | .panel |
| 抽屉 | AppDrawer，IsOpen 双向绑定、Esc/焦点返回 | AppDrawer，同一交互合同 | 现有右栏，使用共享表面角色 |
| 弹窗 | AppDialog，统一所有者/字体/表面 | AppDialog，确认/退出/网页编辑复用 | 无远端页面弹窗桥接 |
| loading | AppLoading | AppLoading | 渲染器已有忙碌状态，共享控件状态样式 |
| 动效 | ComponentMotion + 原侧栏动效 | 共享短 BrushTransition + 原侧栏动效 | 共享 CSS transition/减少动态效果 |

修改变量后运行 `python3 build/generate-design-system.py`，随后运行组件画廊/对比度与页面测试。新增页面不要复制模板；只在共享样式中增加必要变体。业务特定表格单元格、图表图元和 OS 控件保留其专属行为，不把业务算法塞进通用组件。所有内置网页字体只从固定本地白名单提供，CSP 仅允许自身字体资源，不对远程新闻或个人网页注入样式。

字体：随包包含 Adobe Source Sans 3（Regular/Semibold）、Source Serif 4、Source Code Pro 的官方 OFL 字体与完整许可；原始 SHA-256 在 Assets/Fonts/provenance.json。已安装对应 Anthropic 字体时原生端优先使用，未安装时使用明确的 OFL 回退，中文由系统字体回退。官网字体不随项目分发。


已迁移 `AppSelect`；其 macOS 端修复选中 ComboBoxItem 的本地化标签缓存，切换语言不重设 SelectedIndex，不触发额外设置写入。共享输入/选择保留平台弹出、键盘与 IME 行为；图表比较按钮统一为 32px 高度。组件画廊只存在于测试，不向产品页面加入开发说明。

本地验证：Windows 编译通过；共享层 64 项、Chromium 13 项交互、自动化 36 项检查通过。CI 的完整桌面 83 项回归已通过；原生 WebView 冒烟测试的主题背景断言也改为读取同一设计变量。截图位于 build/out/claude-design，为实际 Avalonia/Skia 和 Chromium 渲染，使用测试数据。
