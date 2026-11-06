# winget manifest 草稿（Dynesshely.PerfScope）

本目录是提交到 [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) 的清单草稿，
包含 winget 多文件清单规范要求的三个文件：

| 文件 | 作用 |
| --- | --- |
| `Dynesshely.PerfScope.yaml` | 版本清单（PackageIdentifier / PackageVersion） |
| `Dynesshely.PerfScope.installer.yaml` | 安装清单（安装包 URL、SHA-256、安装类型与范围） |
| `Dynesshely.PerfScope.locale.en-US.yaml` | 语言清单（名称、描述、许可证、标签） |

安装包选择 **NSIS 安装包**（`PerfScope-Setup-<version>-win-x64.exe`，按用户安装、无需管理员）。
portable zip 不能直接交给 winget 安装（需要额外的 nested portable 配置），所以这里不做。

## 待替换的占位符

清单里的版本号目前统一写成 **0.1.3**（待发布版本），SHA-256 是 64 个 0。提交前必须替换：

1. **版本号**：把三个文件里的 `PackageVersion`、`InstallerUrl`、`ReleaseNotesUrl` 中的 `0.1.3`
   全部改成实际发布的版本（与 GitHub tag 去掉 `v` 前缀一致，例如 tag `v0.1.4` → `0.1.4`）。
   三处 `PackageVersion` 必须完全相同。
2. **安装包 URL**：`InstallerUrl` 必须指向已经上传好的 Release 资产，形如
   `https://github.com/Dynesshely/PerfScope/releases/download/v<version>/PerfScope-Setup-<version>-win-x64.exe`。
3. **SHA-256**：先下载安装包，再计算真实哈希替换 `InstallerSha256`（必须 64 位大写十六进制）：

   ```powershell
   Get-FileHash .\PerfScope-Setup-0.1.3-win-x64.exe -Algorithm SHA256 |
       Select-Object -ExpandProperty Hash
   ```

   也可以直接用 Release 的 `SHA256SUMS.txt` 中的对应行。

## 校验

需要装有 winget 的 Windows 环境：

```powershell
# 只校验清单结构与字段
winget validate --manifest .\docs\winget

# 本地试安装（会真的安装，装完用 winget uninstall 卸载）
winget install --manifest .\docs\winget
```

注意：`winget validate` 会检查 SHA-256 是否为合法哈希，占位符 0 能通过格式校验但安装必然失败，
所以替换完再跑本地安装测试。

## 提交到 winget-pkgs

1. 确认该版本已经在 GitHub Releases 发布，且 `InstallerUrl` / `InstallerSha256` 都已替换。
2. Fork `microsoft/winget-pkgs`，把三个文件放到：
   `manifests/d/Dynesshely/PerfScope/<version>/`
3. 本地通过 `winget validate` 后提交 PR，标题形如：
   `New package: Dynesshely.PerfScope version <version>`。
4. 上游 CI 会做一次自动校验与安装测试；按机器人提示修正即可。

## 后续更新

每次发版后，复制本目录到新版本号、替换版本号与 SHA-256，再向 winget-pkgs 提交一个
`Update: Dynesshely.PerfScope version <version>` 的 PR。可以考虑用
[wingetcreate](https://github.com/microsoft/winget-create) 自动完成这一步。
