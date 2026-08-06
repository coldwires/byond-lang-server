// The whole client. The server carries the intelligence; this file only starts it and
// forwards settings as initializationOptions.
const path = require("path");
const vscode = require("vscode");
const { LanguageClient, TransportKind } = require("vscode-languageclient/node");

let client;

function serverOptions() {
    const configured = vscode.workspace.getConfiguration("dm").get("serverPath");

    if (configured && configured.length > 0) {
        return configured.endsWith(".dll")
            ? { command: "dotnet", args: [configured], transport: TransportKind.stdio }
            : { command: configured, args: [], transport: TransportKind.stdio };
    }

    // Development default: the debug build two directories up, so F5 in this folder works
    // against a plain `dotnet build` of the repo.
    const dll = path.join(__dirname, "..", "..", "src", "Dm.Lsp", "bin", "Debug", "net9.0", "dm-lsp.dll");
    return { command: "dotnet", args: [dll], transport: TransportKind.stdio };
}

// Offers "dm: compile <game>.dme" as a build task, so Ctrl+Shift+B compiles and the $dm
// problem matcher turns dm.exe's output into clickable Problems entries. The same defines the
// analysis uses are passed to the compile, so both describe the same program.
function registerCompileTask(context) {
    const provider = {
        provideTasks() {
            const folder = vscode.workspace.workspaceFolders && vscode.workspace.workspaceFolders[0];
            if (!folder) return [];

            const config = vscode.workspace.getConfiguration("dm");
            const compiler = config.get("compilerPath");
            const defines = (config.get("defines") || []).map((d) => `-D${d}`);

            let dme = config.get("environmentFile");
            if (!dme) {
                const fs = require("fs");
                const found = fs
                    .readdirSync(folder.uri.fsPath)
                    .filter((f) => f.toLowerCase().endsWith(".dme"));
                if (found.length === 0) return [];
                dme = found[0];
            }

            // Fixed labels, no .dme in them: a keybinding names a task by exact label, and
            // "dm: compile" has to mean the same thing in every project.
            const compile = new vscode.Task(
                { type: "dm", environmentFile: dme },
                folder,
                "compile",
                "dm",
                new vscode.ProcessExecution(compiler, [...defines, dme], {
                    cwd: folder.uri.fsPath,
                }),
                "$dm"
            );
            compile.group = vscode.TaskGroup.Build;

            // DreamMaker's Run: compile, and only on a clean compile launch the .dmb in Dream
            // Seeker. `start` detaches the game so the task ends while the world runs; cmd.exe is
            // pinned because && means different things across the shells VS Code might default to.
            const seeker = vscode.workspace.getConfiguration("dm").get("seekerPath");
            const dmb = dme.replace(/\.dme$/i, ".dmb");
            const quotedDefines = defines.map((d) => `"${d}"`).join(" ");

            // One outer quote pair with /S is cmd's documented way to carry inner quotes; the
            // empty "" is start's window-title slot. The default task shell can be PowerShell
            // 5.1, where && does not exist, so cmd is pinned explicitly.
            const line =
                `""${compiler}" ${quotedDefines} "${dme}" && start "" "${seeker}" "${dmb}" -trusted"`;

            const run = new vscode.Task(
                { type: "dm", environmentFile: dme, run: true },
                folder,
                "compile and run",
                "dm",
                new vscode.ShellExecution(line, {
                    cwd: folder.uri.fsPath,
                    executable: "cmd.exe",
                    shellArgs: ["/d", "/s", "/c"],
                }),
                "$dm"
            );
            run.group = vscode.TaskGroup.Test;

            return [compile, run];
        },
        resolveTask(task) {
            return task;
        },
    };

    context.subscriptions.push(vscode.tasks.registerTaskProvider("dm", provider));
}

function activate(context) {
    const config = vscode.workspace.getConfiguration("dm");

    registerCompileTask(context);

    client = new LanguageClient(
        "dm",
        "DM Language Server",
        serverOptions(),
        {
            documentSelector: [{ scheme: "file", language: "dm" }],
            initializationOptions: {
                environmentFile: config.get("environmentFile") || undefined,
                defines: config.get("defines") || [],
            },
        }
    );

    client.start();
    context.subscriptions.push({ dispose: () => client && client.stop() });
}

function deactivate() {
    return client ? client.stop() : undefined;
}

module.exports = { activate, deactivate };
