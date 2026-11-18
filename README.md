<p align="center">
  <img src="docs/assets/glint-logo.png" width="160" alt="Glint Windows（非官方移植）">
</p>

<h1 align="center">Glint Windows（非官方移植）</h1>

<p align="center">
  <a href="https://github.com/3171248381kerry-cell/Glint-Windows/releases/download/v0.1.7/Glint-0.1.7-win-x64-setup.exe">
    <img alt="下载 Glint Windows 0.1.7 安装程序" src="https://img.shields.io/github/v/release/3171248381kerry-cell/Glint-Windows?filter=v0.1.7&amp;label=Download&amp;style=flat-square&amp;color=2F7DFF">
  </a>
  <img alt="Windows 10 22H2 或 Windows 11" src="https://img.shields.io/badge/Windows-10_22H2_%2F_11-24292F?style=flat-square">
  <img alt="x64 架构" src="https://img.shields.io/badge/Architecture-x64-57606A?style=flat-square">
  <img alt="本地离线" src="https://img.shields.io/badge/privacy-local_%26_offline-30363D?style=flat-square">
</p>

<p align="center">输入中文时，在拼音候选旁看英语、日语或韩语译文。不替换输入法。</p>

<p align="center">
  <a href="https://github.com/jsogn/Glint">Glint 原项目</a>
  ·
  <a href="https://github.com/jsogn/Glint/issues/7">移植沟通 Issue #7</a>
  ·
  <a href="LICENSE">MIT License</a>
  ·
  <a href="THIRD-PARTY-NOTICES.md">第三方许可</a>
</p>

**这是独立维护的非官方 Windows 移植，与上游项目没有隶属或官方背书关系。** 原作者 Jhon_Walker 在 [jsogn/Glint#7](https://github.com/jsogn/Glint/issues/7) 中接受用「Glint Windows（非官方移植）」描述本项目。原项目仓库：

**[https://github.com/jsogn/Glint](https://github.com/jsogn/Glint)**

macOS 原版的网站、GitHub Releases 和 Homebrew 属于上游项目。需要官方 macOS 版时，请直接使用上面的原仓库。

![Glint 演示封面](docs/assets/glint-video-cover-clean.jpg)

## 下载 Windows 版

当前版本 **0.1.7**，适用于 Windows 10 22H2 / Windows 11 x64。

[直接下载 Windows x64 安装程序](https://github.com/3171248381kerry-cell/Glint-Windows/releases/download/v0.1.7/Glint-0.1.7-win-x64-setup.exe) · [GitHub 发布页](https://github.com/3171248381kerry-cell/Glint-Windows/releases/tag/v0.1.7)

下载后双击安装程序。安装完成后可从开始菜单打开 Glint，立即使用内置英语词典。用微软拼音、微信输入法或豆包输入法输入 `nihao`，停在当前候选上即可查看译文。

## 特点

- **随手可见**：在拼音候选旁显示当前目标语言译文，不打断输入。
- **覆盖词句**：支持常用词语、短语和短句翻译。
- **词性提示**：英语词典译文可显示简洁的英文词性缩写。
- **离线可用**：英语词典随包提供。短句以及日语、韩语翻译可在“插件与模型”中准备对应模型，下载完成后在本机运行。
- **插入与朗读**：可用快捷键或鼠标插入译文，也可手动或自动朗读。
- **自由调整**：可设置译文数量、翻译栏外观、朗读语速和登录启动。
- **隐私友好**：仅读取当前高亮候选，不读取输入框正文或保存翻译历史。

部分功能说明沿用 [Glint 原版 README](https://github.com/jsogn/Glint/blob/c243b461515bf8aa7ee222cb5f68afedf9915cea/README.md)，并按 Windows 版实际支持的功能调整。

## 0.1.7 更新

- 新增 Windows x64 安装程序
- 优化本地模型冷启动速度和内存占用
- 优化英语单词与短语的词典响应
- 优化词典未命中时的模型回退
- 优化翻译栏移动与更新时的资源开销
- 修复微软拼音首次展开时部分高亮候选识别不到的问题
- 修复收起候选栏后译文可能短暂残留的问题
- 优化微信输入法候选识别支持

## 这个仓库

本仓库在上游源码之上增加 Windows 移植，并保留原有 macOS 工程，方便对照和继续同步。使用、快捷键和支持范围见 [Windows 移植说明](Windows/README.md)，实现边界见 [docs/WINDOWS-PORT.md](docs/WINDOWS-PORT.md)。

## 相对上游的修改

Windows 端是按原模块职责重写的平台适配，macOS 的 Swift、AppKit 与 Apple 翻译代码仍留在原目录。

- 新增 `Windows/`：.NET 10 / WPF 应用、托盘、设置页和单行译文栏。
- 候选读取改为 Windows UI Automation，只读取当前高亮候选。
- 词典改为只读打开随附的 CC-CEDICT 与 ECDICT SQLite，使用系统自带的 `winsqlite3.dll`。
- 可选翻译改为本机 Python embeddable 与 CTranslate2 / SentencePiece，模型数据按需下载并校验。
- 朗读改为系统已安装的 SAPI 语音。
- 偏好写入 `%LOCALAPPDATA%\Glint`，不保存候选或翻译历史。

真实输入法的候选显示、切换和插入仍需在目标环境里人工确认。自检通过不等于每一款输入法都已兼容。

## 从源码构建

需要 Windows x64 和 .NET 10 SDK。在仓库根目录执行：

```powershell
.\Windows\scripts\build.ps1
.\Windows\scripts\test.ps1
.\Windows\scripts\run.ps1
```

生成安装程序需要 Inno Setup 7。将 `ISCC.exe` 加入 PATH 后执行：

```powershell
.\Windows\scripts\publish.ps1 -Installer
```

## 开源协议

Glint 源代码继续使用原项目的 [MIT License](LICENSE)。该文件保持原作者版权声明，未作删改：

```text
Copyright (c) 2026 Jhon_Walker
```

Windows 移植中的新增代码与文档同样按这份 MIT License 提供，并保留上述版权与许可声明。MIT License 不覆盖仓库内或安装时取得的词典、翻译模型和第三方运行库；这些部分各自适用下面的许可证。

## 第三方组件

完整来源、分发方式和署名位置见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

| 组件 | 许可证 | 在本仓库中的状态 |
| --- | --- | --- |
| CC-CEDICT 转换后的 SQLite | CC BY-SA 4.0 | 随附，保留 `NOTICE.txt` |
| ECDICT 排序索引 | MIT | 随附，保留上游 `LICENSE.txt` |
| OPUS-MT 中英模型数据 | CC BY 4.0 | 不进入 Git；安装时下载并保留署名 |
| OPUS-MT 中日、中韩模型数据 | CC BY 4.0 | 不进入 Git；安装时下载、转换并写入署名 |
| CTranslate2、SentencePiece | Apache 2.0 | macOS 动态库随附许可文本；Windows 使用官方 wheel |
| Python embeddable 3.12.10 | PSF License | 不进入 Git；安装包内保留其许可证 |
| NumPy | BSD-3-Clause | 随 wheel 安装，不单独再分发 |
| PyYAML、setuptools、fugashi | MIT | 随 wheel 安装，不单独再分发 |
| UniDic Lite 2.1.2 词库 | BSD | 安装时保留 UniDic Consortium 许可 |
| .NET 运行库 | MIT | 仅在发布便携包时复制上游许可证 |
| Qwen、Hy-MT2、Index-Translate | 上游清单写明 Apache 2.0 | 只有清单，没有权重；Windows 移植不下载 |

词典数据库、模型权重和上述运行库不能改标成 MIT。
