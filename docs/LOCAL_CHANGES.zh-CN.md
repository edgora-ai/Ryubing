# Ryubing Canary Local 修改记录

## 基线与标识

- 基于 Ryubing Canary **1.3.351**，上游基线提交：
  `475615f0431b4995b03c930c5089d20f6f3570e0`。
- 初次获取使用保存该历史对象的公开镜像 `SorkoPiko/Ryubing`，没有采用镜像当前
  `master` 的额外优化。官方存档当时访问不可用；这不是官方发行包的二进制一致性证明。
- 本 fork 的本地改动由 Git 提交历史标识。上游基线 SHA **不代表已经包含下列改动**。
- 本地标记：`ryubing-canary-local+475615f.local1`。
- 原 MIT 版权声明、第三方及字体授权文件保持保留；本分支不代表上游官方发布。

## 2026-10-18 至 2026-10-19：独立本地构建

### 1. 应用身份与数据路径

- 应用名称：`Ryubing Canary Local.app`。
- Bundle ID：`local.ryubing.RyubingCanaryLocal`。
- macOS 默认数据目录：`~/Library/Application Support/Ryubing-Canary-Local`。
- 专用 portable 名称：`portable-ryubing-canary-local`，不发现通用 `portable`。
- 保留内部 `Ryujinx` 可执行文件、程序集和 `avares://Ryujinx` 资源标识。
- 显式 `-r` / `--root-data-dir` 优先；非法、缺失、歧义、不可写、受保护重叠或链接路径
  明确失败，不静默回退。保留既有 `-rdct` 参数行为。
- GUI 与 headless 先确定根目录，再初始化配置、日志、账号和原生服务。
- 不读程序旁配置或旧版用户目录，不运行旧 macOS 数据/链接迁移；移除共享偏好域写入。
- 配置、profiles、NAND/SD、缓存、账号、截图、mods、日志及内部 shader 输出沿用所选根。
  profile 名称与内部文件访问检查目录组件及链接边界。
- 四种 key 文件仅从所选根 `system` 读取，不自动借用 `~/.switch`，不导入旧资源。
- 只对新建目录逐级使用私有权限，不修改已有目录权限；日志采用唯一、独占文件名，
  不转写或清理旧版共享日志。

### 2. 配置保存与路径连续性

- 保留 Canary **schema 73** 及原迁移表，不复制其他分支的配置升级或控制器迁移。
- 坏配置恢复和 schema 迁移前保留原始字节；备份失败不掩盖成成功。
- 同目录唯一 staging、flush 和原子替换，保存失败保留上一份有效配置。
- 分离 global/per-title 配置，title ID 限定为 16 位十六进制；全局保存明确写全局路径。
- 账号路径在根初始化后解析；新根没有上次使用账号时按缺省处理。
- 重启及快捷方式携带解析后的绝对根；参数逐项引用，避免空格/引号或重定向根问题。
- macOS 快捷方式使用专用用户 Applications 子目录，不覆盖既有快捷方式或通用安装目录。

### 3. 手动源码更新与安全打包

- 本地版 updater 的公开检查返回禁用/无结果，网络、下载、解包、安装入口在副作用前拒绝。
- 启动更新清理禁用；独立 updater 脚本直接调用也拒绝替换。
- 仅支持此次验证的 self-contained `osx-arm64` 发布与本地打包，不采用继承的 universal
  release/PR wrappers。原 wrapper 仍有上游清理、xattr 和双架构步骤，不是本地构建入口。
- 新 packager 有只读 preflight、输入/输出与链接检查、native 架构及依赖闭包检查，
  校验完整有界 .NET 单文件 bundle、managed payload 和 native 修补/签名前后的输入完整性。
- 新输出必须不存在；失败现场保留，不复用或清理已有输出。
- 保留 required JIT 支持库与原 entitlement，移除默认 document/URL/UTI 关联。
- x64-only SoundIO 明确记录为排除并保留输入；不通过删除输入来伪造 arm64 完整性。
- 本地 ad-hoc 签名并验证，不移除或伪造 quarantine/TCC 等安全设置，也不声称公证。

### 4. UI 字体与原能力边界

- 为 macOS 宿主 UI 添加 CJK-only 系统字体 fallback，不替换 Latin、mono、图标或 emoji 路径。
- 不分发 Apple/Nintendo 字体，不修改 guest 固件字体。
- 按 Avalonia 12 / FluentAvalonia 3 API 编写字形和 navigation 回归，没有盲移植旧 AX workaround。
- 保持该基线的 SDL3、图形和 CPU 核心；没有移植 Nextendo 联机、toast 或手柄实验补丁。
- 上述路径校验不是 OS sandbox，也不表示整个模拟器禁用网络。

## 2026-10-21：标准化归档与 fork 发布

- 源码从临时构建目录归档至正式项目位置；完整 Git 工作树及未提交代码保留。
- 本机迁移逐项检查字节、权限、所有者、mtime、flags、ACL、xattr 和硬链接关系，
  保留完整原始副本及兼容入口，不移动 SDK、游戏、keys、存档或已安装应用。
- 本机原始验证记录放在仓库之外，仅公开本文件的汇总；不上传运行日志、绝对路径清单、
  用户配置、测试输出或私有控制索引。
- 增加醒目的本地分支说明、独立编译/测试指令及生成物/私有输入忽略规则。
- 继承的 Forgejo 发布/triage workflows 含上游目标和凭据引用，不能在新 fork 中直接启用。
  没有将其迁至 GitHub Actions，也不发布二进制 Release。

## 验证记录与未完成项

初次构建实际执行过：

| 范围 | 结果 |
|---|---|
| 聚焦 NUnit 回归 | 213/213，通过，0 失败/跳过 |
| 离线 Python packager 回归 | 44/44，通过 |
| 本机独占发布/防覆盖文件系统回归 | 4/4，通过 |
| arm64 self-contained publish、打包 | 通过 |
| 本机安装逐文件读回与严格签名验证 | 19 个文件通过 |
| 已安装二进制的非法根/参数负例 | 5 个正确返回非零，未创建回退根 |

原始失败也保留，包括测试依赖版本冲突、一次 headless 类型名歧义、旧 Python 解释器
不支持所需 API、首轮中间目录权限问题；修正后才取得上表结果，没有跳过失败项。

**验收边界：**

- Headless 字体测试使用真实 Skia 字形/排版，navigation 测试是自动化控件检查；不能替代
  已安装应用的人工/可见界面验收。
- 实际启动曾观察到主窗口。休眠显示环境出现过原生 render-timer 错误；唤醒重试通过
  该启动点，期间未修改渲染/GPU代码。锁屏和后续中断导致简中菜单、设置保存及重启
  的完整序列尚未验收。
- 游戏、可听声音、左右独立体感、真实振感和重连均**未在本 Canary 上验收**。
- 没有自动复制合法资源；自动测试通过不等于可玩性或发布就绪。

2026-10-21 在正式项目位置重新执行：restore 成功，NUnit **213/213**、离线 packager
**44/44** 全部通过；NUnit 为 0 失败、0 跳过。测试使用新的仓库外私有 fixtures，没有
依赖旧临时源码入口来冒充新路径验证。SDL3、图形和 CPU 核心仍与上游基线无差异。
这些复测没有包含 GUI/游戏/物理手柄验收；现有 solution/继承 CI 不会自动发现这份聚焦测试项目。
