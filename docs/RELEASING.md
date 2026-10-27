# NuGet 自动发布与正式版门禁

版本从 `1.0.0-preview.1` 开始，核心包和兼容包使用相同版本。`Directory.Build.props` 是版本号的唯一来源。

## 首次配置

在 GitHub 仓库 Settings → Secrets and variables → Actions 中创建 `NUGET_API_KEY`。密钥需要允许在 NuGet.org 创建和推送 `PureDb*` 包；不要把密钥写入代码、配置文件或 Git 历史。可以使用 GitHub CLI 的交互输入：

```bash
gh secret set NUGET_API_KEY --repo hnlyf168/PureDb
```

## 发版

1. 修改 `Directory.Build.props` 中的版本，例如 `1.0.0-rc.1`，更新 CHANGELOG。
2. 提交并推送 main，等待 CI 通过。
3. 创建并推送与版本完全一致的标签：

```bash
git tag v1.0.0-rc.1
git push origin v1.0.0-rc.1
```

`Publish NuGet` 自动执行：标签校验 → Windows/Linux 回归、百万次随机变更与差分验证 → Linux 真实部分写入错误恢复 → 三架构 Native AOT → Release 构建 → 包依赖和内容验证 → NuGet 推送 → GitHub 发布。也可在 Actions 页面手动选择对应标签重跑。

版本检查接受 `x.y.z-preview.n`、`x.y.z-rc.n` 和 `x.y.z`。包含预发布后缀的标签创建 GitHub prerelease；无后缀的正式标签创建普通 release。流水线支持正式版不等于已经通过生产验收；正式标签前应按 [发布就绪评估](PRODUCTION-READINESS.md) 核对适用范围和未验证风险。

测试失败会阻止发布。NuGet 已发布的版本不可覆盖；流水线使用 skip-duplicate 支持失败后重试。两个包依次上传，部分上传成功时需要排查后重跑；它们不是一个原子操作。NuGet 完成上传后还需平台验证及索引，搜索结果可能稍后出现。

仓库的基准记录标明实测构建哈希、运行时和硬件。GitHub 流水线从同一提交重新构建发布包，文件哈希可能因构建环境和元数据不同而变化；不把本地实测 DLL 哈希冒充托管流水线包的哈希。

## Native AOT 发布门禁

`aot.yml` 在 Windows x64、Linux x64、Linux ARM64 实际编译运行 7 组功能和至少 298 条扩展差分，拒绝 IL 裁剪 / AOT 编译警告，并检查动态代码不可用。差分数量从实际运行日志提取，不沿用旧版硬编码数量。发布任务等待常规测试和全部 AOT 作业；对打出的两个实际 NuGet 包执行 `package-aot-smoke.py --rid linux-x64` 后才上传，推送命令显式传入仓库 secret `NUGET_API_KEY`。

首次构建需要从 NuGet.org 恢复微软官方 AOT 编译器、ILLink 和目标运行时工具包，因此仓库 NuGet.Config 启用官方源；这不增加 PureDb 或兼容库的第三方运行依赖，包内容检查继续验证该约束。

手动重跑发布时，workflow_dispatch 必须选择对应版本标签（如 v1.0.0-rc.1），不能选择 main；版本检查会拒绝非版本标签。API 密钥只存 GitHub Actions secret，不进入源码或日志。
