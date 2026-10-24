# 预览版自动发布

版本从 `1.0.0-preview.1` 开始，核心包和兼容包使用相同版本。`Directory.Build.props` 是版本号的唯一来源。

## 首次配置

在 GitHub 仓库 Settings → Secrets and variables → Actions 中创建 `NUGET_API_KEY`。密钥需要允许在 NuGet.org 创建和推送 `PureDb*` 包；不要把密钥写入代码、配置文件或 Git 历史。可以使用 GitHub CLI 的交互输入：

```bash
gh secret set NUGET_API_KEY --repo hnlyf168/PureDb
```

## 发版

1. 修改 `Directory.Build.props` 中的版本，例如 `1.0.0-preview.2`，更新 CHANGELOG。
2. 提交并推送 main，等待 CI 通过。
3. 创建并推送与版本完全一致的标签：

```bash
git tag v1.0.0-preview.2
git push origin v1.0.0-preview.2
```

`Publish preview` 自动执行：标签校验 → Windows/Linux 回归与差分验证 → Release 构建 → 包依赖和内容验证 → NuGet 推送 → GitHub 预发布。也可在 Actions 页面手动选择对应标签重跑。

测试失败会阻止发布。NuGet 已发布的版本不可覆盖；流水线使用 skip-duplicate 支持失败后重试。两个包依次上传，部分上传成功时需要排查后重跑；它们不是一个原子操作。NuGet 完成上传后还需平台验证及索引，搜索结果可能稍后出现。

仓库的基准记录标明实测构建哈希、运行时和硬件。GitHub 流水线从同一提交重新构建发布包，文件哈希可能因构建环境和元数据不同而变化；不把本地实测 DLL 哈希冒充托管流水线包的哈希。
