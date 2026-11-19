# 项目约定

@C:\Users\23040\.codex\RTK.md

## 版本管理

- 当前正式发布基线为 1.0.0。
- 后续版本必须遵循 VERSIONING.md：不兼容变更升主版本；兼容新增功能升次版本；兼容修复、小的性能优化和界面细节调整升修订号。提高某一级时，较低级数字归零。
- 版本来源为 source/AppVersion.cs 的 AppVersion.Current；同步维护文档及 vX.Y.Z 标签。
- 发布后不重写或移动已有版本标签。
