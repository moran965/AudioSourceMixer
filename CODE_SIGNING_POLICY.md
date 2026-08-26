# Code signing policy / 代码签名政策

Free code signing provided by SignPath.io, certificate by SignPath Foundation.

免费代码签名由 SignPath.io 提供，证书由 SignPath Foundation 提供。

## Project identity / 项目标识

- Repository / 仓库：<https://github.com/moran965/AudioSourceMixer>
- License / 许可证：MIT
- Privacy / 隐私：[English](docs/privacy.md) · [简体中文](docs/privacy.zh-CN.md)
- Security reports / 安全报告：[SECURITY.md](SECURITY.md)

No legal name or organization is inferred from the account handle. The GitHub account below is the project's operational identity for source control and signing approvals.

不根据账号名称推断或编造维护者法定姓名或组织。以下 GitHub 账号仅作为源码管理和签名审批的项目操作身份。

## Roles / 角色

- Committer / 提交者：[@moran965](https://github.com/moran965)
- Reviewer / 审核者：[@moran965](https://github.com/moran965)
- Approver / 签名批准者：[@moran965](https://github.com/moran965)

This is currently a single-maintainer project. External contributions require review before merge. A signing request is never approved solely because an automated build completed; the Approver checks the tag, workflow origin, test/audit results, release scope, and generated artifact inventory.

本项目目前由单一维护者管理。外部贡献合并前必须审核。自动构建完成不等于自动批准签名；批准者必须核对标签、工作流来源、测试与审计结果、发行范围以及生成的产物清单。

## Build and approval rules / 构建与批准规则

1. Release inputs must reference an existing annotated tag whose product, file, and extension versions agree.
2. Release files are built on GitHub-hosted Windows runners from that tag; local binaries are never submitted as official Release assets.
3. The complete reachable Git history and release tree are scanned for secrets, and automated .NET/Node tests plus repository audits must pass.
4. Trusted signing signs `AudioSourceMixer.exe` and `AudioSourceMixer.NativeHost.exe` before installer packaging, then signs the final setup. Every signed PE must verify as `Valid`, include a real signer, and carry an RFC 3161 timestamp.
5. The Approver manually approves each production signing request and confirms that only the documented release assets are published.
6. SignPath API tokens, certificates, and private keys are never committed or included in build artifacts. GitHub environment protection and short-lived workflow credentials are used where available.
7. SHA-256, SPDX SBOM, actual Authenticode state, and GitHub Artifact Attestation are published with each binary release. GitHub provenance is not represented as Authenticode.

1. Release 输入必须指向现有 annotated tag，且产品版本、文件版本和扩展版本一致。
2. 正式文件必须由 GitHub 托管的 Windows runner 从该标签构建；本地二进制文件不得作为正式 Release 资产提交。
3. 必须扫描全部可达 Git 历史和发行树，并通过 .NET/Node 自动化测试及仓库审计。
4. 可信签名先对 `AudioSourceMixer.exe` 与 `AudioSourceMixer.NativeHost.exe` 签名，再组装并签名最终安装程序；每个签名 PE 必须为 `Valid`，具有真实签名者和 RFC 3161 时间戳。
5. 批准者逐次人工批准生产签名请求，并确认只发布文档规定的发行资产。
6. SignPath API token、证书和私钥不得提交或进入构建产物；可用时使用 GitHub 环境保护和短期工作流凭据。
7. 每次二进制发行同时发布 SHA-256、SPDX SBOM、真实 Authenticode 状态和 GitHub Artifact Attestation；不得把 GitHub 来源证明描述为 Authenticode。

## Initial unsigned release / 首次未签名发行

SignPath Foundation requires application and human approval and may require an already released project. If approval is not available for the initial v1.0.0 build, the workflow's explicit unsigned fallback may publish it with prominent bilingual warnings and verified `NotSigned` status. After trusted signing becomes available, the v1.0.0 assets will not be silently replaced; a new patch release will carry the trusted signature.

SignPath Foundation 需要申请和人工审批，并可能要求项目已经发布。如果首次 v1.0.0 构建尚未获得批准，工作流仅可通过显式未签名回退发布，并醒目标注中英文风险及核验后的 `NotSigned` 状态。以后获得可信签名后不会静默替换 v1.0.0 资产，而会通过新的补丁版本发行可信签名文件。

The OSS signing application was submitted on 2026-08-27 and is awaiting SignPath Foundation's human review. No Foundation certificate, production signing policy, or API credential is available yet, so the existing v1.0.0 release remains truthfully `NotSigned`.

开源签名申请已于 2026-08-27 提交，正在等待 SignPath Foundation 人工审核。当前尚无 Foundation 证书、生产签名策略或 API 凭据，因此现有 v1.0.0 发行仍如实保持 `NotSigned` 状态。
