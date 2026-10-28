# MSSQL Tool 发布说明 / Release Notes

> 发布脚本 `tools\Publish-Release.cmd` 只读取当前版本对应的 `## <版本号>` 段落作为 Release 正文。
> 每次调整后直接改这一节即可；重新上传同一版本会同步更新已有 Release 的说明。
>
> The manual release script uses the `## <version>` section below as the GitHub release body.
> Re-running it for the same version patches the existing release, including this text.

## 5.371

### 中文

- **定位列不再记忆搜索内容**：下拉列表只显示当前结果集的列名，不再把以前搜过的词放在最上面；定位成功后输入框自动清空，换结果集时也会清空。
- **统计面板按钮改为图标**：固定列（图钉）、取消固定（带斜线的图钉）、定位列（十字准星）、查找值（放大镜）。图标带中英文工具提示，鼠标悬停即可看到名称。
- **版本号改为三位小数**：从本版起使用 `5.371` 这样的格式，后续为 `5.372`、`5.373`……，每一次小改动只增加最后一位；「关于」页也显示 `5.371`，不再是 `5.371.0.0`。
- **发布改为手动**：日常构建不再自动上传 GitHub Release；需要发布时双击 `tools\Publish-Release.cmd`，由你自行决定是否上传。

### English

- **Locate column no longer remembers searches**: the dropdown lists only the current result columns, previously searched terms are gone, and the box is cleared after a successful jump and whenever the result set changes.
- **Icon buttons in the statistics panel**: pin (freeze), pin-off (unfreeze), crosshair (locate column) and magnifier (find value), each with a localized tooltip.
- **Three-decimal version numbers**: this release is `5.371`, followed by `5.372`, `5.373`, ... so routine changes only bump the last digit; the About page now shows `5.371` instead of `5.371.0.0`.
- **Manual releases**: regular builds no longer upload to GitHub Releases. Double-click `tools\Publish-Release.cmd` when you decide to publish.

### 安装 / Install

关闭所有 SSMS 窗口，双击下方的 `MSSQLTool_SSMS22_5.371.vsix` 安装，然后重新启动 SSMS。
Close all SSMS windows, double-click the `MSSQLTool_SSMS22_5.371.vsix` asset below, then restart SSMS.
