# 2D换装骨骼自动重绑工具

Unity 2D 换装 / 骨骼自动重绑小工具，面向 Sprite Resolver + Sprite Library Asset 工作流。

它可以批量处理多个角色对象，帮助你快速：

- 添加 Sprite Library Asset 相关组件
- 给子对象自动挂 Sprite Resolver / 重绑脚本
- 批量切换或保留指定皮肤骨骼
- 记录操作日志，方便排查绑定问题

适合把美术提供的骨骼换装资源整理成可复用的 Unity 编辑器工具，在不同电脑、不同项目中保持一致的操作流程。

## 使用方式

1. 把整个仓库上传到 GitHub。
2. 在 Unity 里打开 `Window > Package Manager`。
3. 点击左上角 `+`，选择 `Add package from git URL...`。
4. 粘贴仓库的 HTTPS 克隆地址，例如：

   `https://github.com/CRboxman/unity-2d-sprite-skin-rebinder.git`

5. 点击 Add，Unity 会自动下载并安装。

如果仓库是私有的，先确保本机已经登录 GitHub，或者能通过 GitHub 凭据访问该仓库。

## 包结构说明

这个仓库已经放了 `package.json`，所以它可以被 Unity 当作 UPM 包识别。

- 当前仓库里，核心脚本和示例资源还放在 `2D换装骨骼自动重绑工具/` 下。
- 以后如果你想做成更标准的 Unity 包，推荐整理成：
  - `Runtime/`：运行时/通用逻辑
  - `Editor/`：编辑器按钮、窗口、菜单
  - `Samples~/`：示例资源
- `README.md`：安装说明
- `使用说明.txt`：更细的操作说明

如果以后你把包放进仓库的子目录里，Package Manager 里就要用：

`https://github.com/你的仓库.git?path=/Packages/你的包目录`

如果包根目录本身就是仓库根目录，就直接粘 Git URL，不用加 `?path=`.

## 以后做这类工具的推荐流程

1. 先把工具做成一个独立 Unity 包。
2. 根目录放 `package.json`。
3. `Runtime/` 放核心代码，`Editor/` 放编辑器代码。
4. 需要演示素材时，放到 `Samples~/`，不要混进主逻辑目录。
5. 推到 GitHub。
6. 其他电脑直接用 Package Manager 的 Git URL 导入。

## 说明

本工具保留 `.meta` 文件，适合直接放进 GitHub 仓库做版本管理和跨电脑同步。
