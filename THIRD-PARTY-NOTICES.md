# 第三方许可与署名

核对日期：2026-11-17。

[Glint 原项目](https://github.com/jsogn/Glint) 的 MIT License 只覆盖其源代码与文档。下面这些词典、模型和运行库各自保留原许可证。Windows 移植使用它们时，按原许可署名、标明修改，并让许可文本随对应数据一起分发。

## 随仓库分发

### CC-CEDICT

- 数据：`Resources/Plugins/dict-cedict/cedict.sqlite3`
- 来源：<https://cc-cedict.org/>
- 许可证：[CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/)
- 署名文件：`Resources/Plugins/dict-cedict/NOTICE.txt`
- 修改：上游词表被转换成只读 SQLite。转换后的数据库仍以 CC BY-SA 4.0 提供。

### ECDICT

- 数据：`Resources/Plugins/dict-ecdict/ecdict_rank.sqlite3`
- 来源：<https://github.com/skywind3000/ECDICT>
- 许可证：MIT
- 版权声明：`Copyright (c) 2025 Linwei`
- 署名文件：`Resources/Plugins/dict-ecdict/LICENSE.txt`
- 核对：该文件与上游 `LICENSE` 一致。
- 修改：仓库中的 SQLite 是词频和义项排序索引，原许可声明随数据库保留。

### CTranslate2 与 SentencePiece

- 文件：`Resources/CTranslate2Runtime/bin/libctranslate2.dylib`、`libsentencepiece.0.0.0.dylib`、`libsentencepiece.0.dylib`
- 来源：<https://github.com/OpenNMT/CTranslate2>、<https://github.com/google/sentencepiece>
- 许可证：[Apache License 2.0](Resources/CTranslate2Runtime/LICENSE.Apache-2.0.txt)
- 署名文件：`Resources/CTranslate2Runtime/ATTRIBUTION.md`
- 核对：CTranslate2 仓库根目录没有单独的 `NOTICE`。`libglintct2.dylib` 是本仓库的桥接库，适用根目录 MIT License。

Windows 移植不把这些 macOS 动态库打进 Windows 包。Windows 在准备本地模型时下载固定版本的官方 wheel，并完整解包，wheel 内的许可证留在 `site-packages`。

| Windows 运行库 | 版本 | 许可证 |
| --- | --- | --- |
| CTranslate2 | 4.8.3 | Apache 2.0 |
| SentencePiece | 0.2.2 | Apache 2.0 |
| Python embeddable | 3.12.10 | [PSF License](https://docs.python.org/3/license.html) |
| NumPy | 2.3.3 | BSD-3-Clause |
| PyYAML | 6.0.3 | MIT |
| setuptools | 80.9.0 | MIT |

固定地址和 SHA256 在 `Windows/Glint.Windows/Runtime/runtime-lock.json`。Python 官方 embeddable 压缩包会整包解压，其中的 `LICENSE.txt` 保留在安装目录。

发布便携包时，`Windows/scripts/publish.ps1` 会把 .NET 运行库自带的 `LICENSE` 与 `THIRD-PARTY` 文件复制到 `Data/DotNetLicenses/`。.NET 运行库适用 MIT License。SQLite 使用 Windows 自带的 `winsqlite3.dll`，应用不另行分发 SQLite。

## 安装时下载

这些数据不进入 Git，也不在主程序压缩包里。

### OPUS-MT 中英

- 模型：`Helsinki-NLP/opus-mt-zh-en`
- 来源：<https://huggingface.co/Helsinki-NLP/opus-mt-zh-en>
- 数据包：上游发布的 `mt-opus-zh-en-0.1.0.zip`
- 许可证：[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/)
- 署名：安装允许列表包含 `mt-opus-zh-en/ATTRIBUTION.md`
- 修改：Glint 使用的是 CTranslate2 int8 转换结果，署名文件中已说明。

该地址只提供模型数据。它不是本 Windows 移植的程序下载或支持渠道。

### OPUS-MT 中日与中韩

- 中日：Tatoeba-MT `zho-jpn`，`opusTCv20210807`
- 中韩：Tatoeba-MT `zho-kor`，`opusTCv20210807`
- 项目：<https://github.com/Helsinki-NLP/Tatoeba-Challenge>
- 许可证：CC BY 4.0
- 修改：安装脚本在本机转换为 CTranslate2 int8，并在模型目录旁写入 `ATTRIBUTION.md`。

### 日语读音

- fugashi 1.5.2：MIT，<https://github.com/polm/fugashi>。wheel 完整解包，许可留在包内。
- unidic-lite 1.0.8 的代码：MIT 或 WTFPL，版权 Paul McCann。
- 其中的 UniDic 2.1.2 词库：BSD，版权 2011–2017 The UniDic Consortium，<https://github.com/polm/unidic-lite/blob/master/LICENSE.unidic>。
- 安装脚本把 `LICENSE` 和 `LICENSE.unidic` 复制到已安装的 `unidic_lite` 目录。词库作者的名称不用于为本移植背书。

## 只有清单、没有权重

以下条目来自上游插件清单，权重不在本仓库中，Windows 移植也不下载它们：

| 清单 | 声明的许可证 | 权重位置 |
| --- | --- | --- |
| Qwen3 0.6B / 1.7B / 4B | Apache 2.0 | Hugging Face 上的 MLX 量化仓库 |
| Hy-MT2 1.8B | Apache 2.0 | Hugging Face 上的 MLX 量化仓库 |
| Index-Translate 2B | Apache 2.0 | Hugging Face 上的 MLX 量化仓库 |

这些清单保留，是因为上游 macOS 源码仍在本仓库中。将来若实际再分发任一权重，需要另行核对当时的模型许可、署名和 NOTICE。
