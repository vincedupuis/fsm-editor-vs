# Code Generation

FSM Editor for Visual Studio generates source code with [Jinja](https://jinja.palletsprojects.com/) templates, running **`fsm`**, the same command-line generator as FSM Editor for VS Code and for JetBrains IDEs. `fsm` is [fsm-cli](https://github.com/vincedupuis/fsm-cli), in Rust, with templates rendered by [MiniJinja](https://github.com/mitsuhiko/minijinja). The extension doesn't reimplement code generation: it runs `fsm.exe write`, which it bundles, and shows the results in Visual Studio. The same templates therefore produce the same code from Visual Studio, VS Code, a JetBrains IDE, a terminal or a CI build.

Handlebars templates (`.hbs`), which earlier versions used, are no longer supported: `fsm` refuses them and says so. [Coming from the Handlebars templates](https://github.com/vincedupuis/fsm-cli#coming-from-the-handlebars-templates) shows how to convert one to Jinja.

- [From Visual Studio](#from-visual-studio)
- [From the Command Window or a key binding](#from-the-command-window-or-a-key-binding)
- [From the command line and builds](#from-the-command-line-and-builds)
- [Which fsm is used](#which-fsm-is-used)
- [Templates and the code model](#templates-and-the-code-model)

## From Visual Studio

Run **Generate Code...** from the diagram toolbar (**Code**), the context menu of a `.fsm` file in Solution Explorer, the context menu of the document tab, or **Tools › FSM Editor**. A dialog asks for:

1. **The template**: the templates bundled with `fsm` (`ts` and `sml`, as *bundled template*), any `*.jinja` file of the solution, or **Browse...**.
2. **The output folder.** Generated files are overwritten every time.

The last template, and the last output folder of each machine, are remembered.

The generator reads the files on disk, so open `.fsm` documents with unsaved changes are saved first (Tools › Options › FSM Editor › *Save .fsm documents before generating*). Machines used by submachine states are read and validated too, but only the chosen machine is generated.

Results:

- The **FSM Code Generation** output pane shows the command that ran and every file written.
- Problems go to the **Error List** (source *FSM Code Generation*). Double-click one to open the machine.
- Generation stops when the machine has validation errors. Nothing is written then.
- The status bar reports how many files were written.

## From the Command Window or a key binding

`FSM.GenerateCode` takes the generator's own options, to skip the dialog. It uses the active or selected `.fsm` file. Paths are absolute or relative to the solution folder, and a bare template name is a bundled template: `ts`, or `ts.jinja` unless the solution folder has a `ts.jinja`:

```
FSM.GenerateCode -t ts -o src\generated
FSM.GenerateCode --template templates\cs.jinja --out "src\generated code"
```

Bind `FSM.GenerateCode` to a key in Tools › Options › Environment › Keyboard. Key bindings can't pass arguments, so the command shows the dialog then.

## From the command line and builds

The bundled program is a single executable without dependencies, in the `cli` folder of the extension's install directory, with the bundled templates in `cli\templates`. It's also published for Windows, macOS and Linux on the [fsm-cli releases](https://github.com/vincedupuis/fsm-cli/releases):

```sh
fsm write "models/**/*.fsm" --template ts --out src/generated
```

Exit codes: `0` success, `1` errors in the machines or the template (with `--check`, also files missing or out of date), `2` bad usage. Problems are printed as `file:line: severity: message`, which MSBuild and CI logs understand. With `--check`, `fsm write` writes nothing and reports the generated files that are missing, or out of date because their `.fsm` file's model hash has changed since ([details](https://github.com/vincedupuis/fsm-cli#fsm-write)). To generate code on every build, call it from an MSBuild target:

```xml
<Target Name="GenerateStateMachines" BeforeTargets="BeforeBuild" Inputs="@(StateMachine)" Outputs="$(IntermediateOutputPath)fsm.stamp">
  <Exec Command="fsm write &quot;%(StateMachine.FullPath)&quot; -t ts -o &quot;$(ProjectDir)generated&quot;" />
  <Touch Files="$(IntermediateOutputPath)fsm.stamp" AlwaysCreate="true" />
</Target>
<ItemGroup>
  <StateMachine Include="models\*.fsm" />
</ItemGroup>
```

## Which fsm is used

In this order:

1. The path set in Tools › Options › FSM Editor › *fsm executable*, when set.
2. The executable bundled with the extension (`cli\fsm.exe`), with its templates in `cli\templates`.
3. An `fsm.exe` on the `PATH`.

Set the option to use a newer generator than the bundled one, or your own build.

## Templates and the code model

A template is a Jinja file, with optional front matter. One template writes any number of files per machine through `{% filter file("path") %}` blocks. Templates can't run code: every filter and function is built into the generator.

The template language is documented in [fsm-cli's README](https://github.com/vincedupuis/fsm-cli#writing-a-template). The bundled templates, TypeScript (`ts`) and C++17 for Boost.SML (`sml`), the files they write, the [code model](https://github.com/vincedupuis/fsm-cli/blob/main/docs/CODEGEN.md#code-model-reference) the templates receive, and the [semantics of the generated code](https://github.com/vincedupuis/fsm-cli/blob/main/docs/CODEGEN.md#semantics-of-the-generated-code) are documented in [fsm-cli's docs/CODEGEN.md](https://github.com/vincedupuis/fsm-cli/blob/main/docs/CODEGEN.md). Templates in your solution are listed in the Generate Code dialog, so you can keep them next to your models.
