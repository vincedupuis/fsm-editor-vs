# Code Generation

FSM Editor for Visual Studio generates source code with the **same `fsm` command-line generator** as FSM Editor for VS Code. The extension doesn't reimplement code generation: it runs `fsm.exe`, which it bundles, and shows the results in Visual Studio. The same templates therefore produce the same code from Visual Studio, VS Code, a terminal or a CI build.

- [From Visual Studio](#from-visual-studio)
- [From the Command Window or a key binding](#from-the-command-window-or-a-key-binding)
- [From the command line and builds](#from-the-command-line-and-builds)
- [Which fsm is used](#which-fsm-is-used)
- [Templates and the code model](#templates-and-the-code-model)

## From Visual Studio

Run **Generate Code...** from the diagram toolbar (**Code**), the context menu of a `.fsm` file in Solution Explorer, the context menu of the document tab, or **Tools › FSM Editor**. A dialog asks for:

1. **The template**: the templates bundled with the generator (`ts`, `sml`), any `*.hbs` file of the solution, or **Browse...**.
2. **The output folder.** Generated files are overwritten every time.

The last template, and the last output folder of each machine, are remembered.

The generator reads the files on disk, so open `.fsm` documents with unsaved changes are saved first (Tools › Options › FSM Editor › *Save .fsm documents before generating*). Machines used by submachine states are read and validated too, but only the chosen machine is generated.

Results:

- The **FSM Code Generation** output pane shows the command that ran and every file written.
- Problems go to the **Error List** (source *FSM Code Generation*). Double-click one to open the machine.
- Generation stops when the machine has validation errors. Nothing is written then.
- The status bar reports how many files were written.

## From the Command Window or a key binding

`FSM.GenerateCode` takes the generator's own options, to skip the dialog. It uses the active or selected `.fsm` file. Paths are absolute or relative to the solution folder, and a bare template name is a bundled template:

```
FSM.GenerateCode -t ts -o src\generated
FSM.GenerateCode --template templates\cpp.hbs --out "src\generated code"
```

Bind `FSM.GenerateCode` to a key in Tools › Options › Environment › Keyboard. Key bindings can't pass arguments, so the command shows the dialog then.

## From the command line and builds

The bundled program is a standalone executable (no Node.js needed), in the `cli` folder of the extension's install directory. It's also published as an archive for Windows, macOS and Linux with FSM Editor for VS Code:

```sh
fsm "models/**/*.fsm" --template ts --out src/generated
```

Exit codes: `0` success, `1` errors in the machines or the template, `2` bad usage. Problems are printed as `file:line: severity: message`, which MSBuild and CI logs understand. To generate code on every build, call it from an MSBuild target:

```xml
<Target Name="GenerateStateMachines" BeforeTargets="BeforeBuild" Inputs="@(StateMachine)" Outputs="$(IntermediateOutputPath)fsm.stamp">
  <Exec Command="fsm &quot;%(StateMachine.FullPath)&quot; -t ts -o &quot;$(ProjectDir)generated&quot;" />
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
3. An `fsm.exe` or `fsm.cmd` on the `PATH` (for example from `npm link` in a clone of FSM Editor for VS Code).

Set the option to use a newer generator than the bundled one, or your own build.

## Templates and the code model

Templates are Handlebars files, optionally with YAML front matter. One template writes any number of files per machine through `{{#file "path"}}` blocks. They can't run code: every helper is built into the generator.

The bundled templates, TypeScript (`ts`) and [C++17 for Boost.SML](https://github.com/vincedupuis/fsm-editor-vscode/blob/main/docs/CODEGEN.md#the-boostsml-template) (`sml`), writing templates for other languages (C#, C++, Java, Python...), the helpers, the code model the templates receive, and the execution semantics of the generated code are documented with the generator, in [FSM Editor for VS Code's CODEGEN.md](https://github.com/vincedupuis/fsm-editor-vscode/blob/main/docs/CODEGEN.md). Templates in your solution are listed in the Generate Code dialog, so you can keep them next to your models.
