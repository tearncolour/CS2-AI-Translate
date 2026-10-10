# Contributing / 参与贡献

Thanks for helping! / 感谢帮忙！

## How to contribute / 怎么贡献

All changes go through pull requests; only the author merges into `main`.
所有改动都通过 Pull Request 提交，只有作者可以合并到 `main`。

1. Fork this repository and create a branch from `main`. / Fork 本仓库，从 `main` 新建分支。
2. Make your change, build it and run the tests (see the README). / 修改后编译并运行测试（见 README）。
3. Open a pull request and describe what you changed and how you tested it. / 提交 Pull Request，说明改了什么、怎么测试的。

## Improving the settings-page text / 改进设置页的翻译

The settings page ships in all 12 game languages. English and Simplified Chinese live in `Locale/Locales.cs`; every other language is a JSON file in `Locale/Languages/` (for example `de-DE.json`). To fix a wording, edit the value for that key and open a pull request. Keep `{0}`, `{1}`… placeholders and `\n` line breaks as they are; the tests check this.
设置页预置游戏的全部 12 种语言。英文和简体中文在 `Locale/Locales.cs`，其他语言在 `Locale/Languages/` 下的 JSON 文件里（如 `de-DE.json`）。要改某个说法，直接改对应键的值并提交 PR；`{0}`、`{1}` 等占位符和 `\n` 换行要保留，测试会检查。

## Translation feedback / 翻译问题反馈

If a translation is wrong or text is left untranslated, please open an issue with:
如果有翻错或漏翻，请提 Issue 并写上：

- your game language / 你的游戏语言
- the mod and the text involved / 相关的 mod 和文字
- `Logs/AITranslate.Mod.log` if possible / 能附上 `Logs/AITranslate.Mod.log` 更好

## License / 许可证

By contributing you agree that your contribution is licensed under GPL-3.0, like the rest of the project.
提交贡献即表示你同意贡献内容和本项目一样以 GPL-3.0 发布。
