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

// The object tree, browsed lazily through dm/objectTree. Every node reports childCount, which is
// how many children EXIST rather than how many this response carried - so an expander is drawn
// from one call instead of costing a round trip per collapsed row.
class ObjectTreeProvider {
    constructor() {
        this._changed = new vscode.EventEmitter();
        this.onDidChangeTreeData = this._changed.event;
    }

    refresh() {
        this._changed.fire();
    }

    getTreeItem(node) {
        const item = new vscode.TreeItem(
            node.name || node.path,
            node.childCount > 0
                ? vscode.TreeItemCollapsibleState.Collapsed
                : vscode.TreeItemCollapsibleState.None
        );

        item.description = `${node.varCount}v ${node.procCount}p`;
        item.tooltip = node.parentType
            ? `${node.path}\ninherits ${node.parentType}`
            : node.path;
        item.contextValue = "dmType";

        // The objectTree response carries no declaration site, so navigation goes through
        // workspace/symbol with the `#` type filter rather than through a wider schema. A builtin
        // is declared nowhere, so it stays unclickable instead of opening the wrong thing.
        if (!node.builtin && node.declared) {
            item.command = {
                command: "dm.revealType",
                title: "Go to Declaration",
                arguments: [node],
            };
        }

        return item;
    }

    async getChildren(node) {
        if (!client) return [];

        try {
            // Depth 1: this node and one level under it. Deeper would fetch subtrees nobody has
            // expanded, on a project where /obj alone can carry thousands.
            const answer = await client.sendRequest("dm/objectTree", {
                path: node ? node.path : "/",
                depth: 1,
            });

            return (answer && answer.children) || [];
        } catch {
            // A workspace with no .dme answers an error rather than a tree; an empty panel is the
            // honest rendering of that, not a popup on every expand.
            return [];
        }
    }
}

// The .dme in the status bar, because which one is analysed decides every answer the server
// gives and today it is invisible until something resolves wrongly.
function registerEnvironmentPicker(context) {
    const item = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 100);
    item.command = "dm.selectEnvironmentFile";
    context.subscriptions.push(item);

    const show = () => {
        const configured = vscode.workspace.getConfiguration("dm").get("environmentFile");
        item.text = configured ? `$(check) ${path.basename(configured)}` : "$(question) no .dme";
        item.tooltip = configured
            ? `Analysing ${configured}. Click to change.`
            : "No .dme chosen; the server picks the first one it finds. Click to choose.";
        item.show();
    };

    context.subscriptions.push(
        vscode.commands.registerCommand("dm.selectEnvironmentFile", async () => {
            const found = await vscode.workspace.findFiles("**/*.dme", "**/node_modules/**", 64);

            if (found.length === 0) {
                vscode.window.showWarningMessage("No .dme files found in this workspace.");
                return;
            }

            const folder = vscode.workspace.workspaceFolders && vscode.workspace.workspaceFolders[0];
            const picked = await vscode.window.showQuickPick(
                found.map((uri) => ({
                    label: path.basename(uri.fsPath),
                    description: folder
                        ? path.relative(folder.uri.fsPath, uri.fsPath)
                        : uri.fsPath,
                    uri,
                })),
                { placeHolder: "Which .dme should be analysed?" }
            );

            if (!picked) return;

            const relative = folder
                ? path.relative(folder.uri.fsPath, picked.uri.fsPath)
                : picked.uri.fsPath;

            await vscode.workspace
                .getConfiguration("dm")
                .update("environmentFile", relative, vscode.ConfigurationTarget.Workspace);

            // The .dme is an initializationOption, so it is read once at startup - changing it
            // means restarting the server rather than notifying it.
            vscode.window.showInformationMessage(
                `DM: analysing ${relative}. Reload the window to apply.`
            );
        })
    );

    context.subscriptions.push(
        vscode.workspace.onDidChangeConfiguration((event) => {
            if (event.affectsConfiguration("dm.environmentFile")) show();
        })
    );

    show();
}

function activate(context) {
    const config = vscode.workspace.getConfiguration("dm");

    registerCompileTask(context);
    registerEnvironmentPicker(context);

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

    // Tick or untick the active file in the .dme, the way DreamMaker's file tree does. The server
    // returns an EDIT rather than writing the .dme, so this applies as a WorkspaceEdit and is safe
    // against a .dme the user has open with unsaved changes.
    context.subscriptions.push(
        // The icon browser. dm/iconStates has been served since M8 and nothing asked for it, which
        // is the same shape as dm/objectTree sitting unused for two milestones while a tree panel
        // was the headline "missing" feature. A row is not parity until something calls it.
        vscode.commands.registerCommand("dm.browseIconStates", async () => {
            let uri = vscode.window.activeTextEditor?.document?.uri;

            if (!uri || !uri.fsPath.toLowerCase().endsWith(".dmi")) {
                const picked = await vscode.window.showOpenDialog({
                    canSelectMany: false,
                    openLabel: "Browse states",
                    filters: { "DreamMaker icons": ["dmi"] },
                });

                if (!picked || picked.length === 0) {
                    return;
                }

                uri = picked[0];
            }

            const answer = await client.sendRequest("dm/iconStates", { uri: uri.toString() });

            // "isDmi": false is an ANSWER, not a failure - zero-byte .dmi files ship in real games,
            // and so do plain PNGs saved under the extension. Say which, rather than showing an
            // empty list that reads as a broken command.
            if (!answer || answer.isDmi !== true) {
                vscode.window.showWarningMessage(
                    `${uri.fsPath} is not a DreamMaker icon. Zero-byte .dmi files and plain PNGs ` +
                        "saved under that extension both look like this.",
                );
                return;
            }

            const states = answer.states || [];

            if (states.length === 0) {
                vscode.window.showInformationMessage("That icon declares no states.");
                return;
            }

            const size =
                answer.width && answer.height ? `${answer.width}x${answer.height}` : "size unstated";

            // A NAME IS NOT A KEY: one name can appear twice, once with movement set, and DM picks
            // between them at runtime. Keying a map by name would silently drop half of those, so
            // the list stays an array and the movement ones are labelled.
            const items = states.map((state) => ({
                label: state.name === "" ? "(default)" : state.name,
                description: [
                    `${state.dirs} dir${state.dirs === 1 ? "" : "s"}`,
                    `${state.frames} frame${state.frames === 1 ? "" : "s"}`,
                    state.movement ? "movement" : null,
                    state.rewind ? "rewind" : null,
                ]
                    .filter(Boolean)
                    .join(", "),
            }));

            const chosen = await vscode.window.showQuickPick(items, {
                title: `${states.length} state(s), ${size}`,
                placeHolder: "Pick a state to copy its name",
                matchOnDescription: true,
            });

            if (chosen) {
                // The empty name is the default state and is completely ordinary. Copying the
                // literal empty string is what a caller actually wants to paste.
                const name = chosen.label === "(default)" ? "" : chosen.label;
                await vscode.env.clipboard.writeText(name);
                vscode.window.showInformationMessage(`Copied icon_state "${name}"`);
            }
        }),

        vscode.commands.registerCommand("dm.toggleFileInEnvironment", async () => {
            const editor = vscode.window.activeTextEditor;
            if (!editor || editor.document.languageId !== "dm") {
                vscode.window.showWarningMessage("Open a .dm file to tick or untick it.");
                return;
            }

            const uri = editor.document.uri;
            const ticked = await client.sendRequest("dm/fileInProject", {
                textDocument: { uri: uri.toString() },
            });

            const method = ticked && ticked.inProject ? "dm/untickFile" : "dm/tickFile";
            const answer = await client.sendRequest(method, {
                textDocument: { uri: uri.toString() },
            });

            if (!answer || answer.refusal !== "none") {
                // Every refusal has a cause; say which rather than failing silently.
                const why = {
                    conditional:
                        "The .dme's include block contains #if/#endif. A line inside a conditional " +
                        "does not mean the file is in the build, so there is no correct edit.",
                    noBlock: "The .dme has no // BEGIN_INCLUDE block to edit.",
                    noChange: "Already in that state.",
                }[(answer && answer.refusal) || "noBlock"];

                vscode.window.showWarningMessage(`DM: ${why}`);
                return;
            }

            const edit = new vscode.WorkspaceEdit();
            const range = new vscode.Range(
                answer.range.start.line,
                answer.range.start.character,
                answer.range.end.line,
                answer.range.end.character
            );

            edit.replace(vscode.Uri.parse(answer.uri), range, answer.text);
            await vscode.workspace.applyEdit(edit);

            vscode.window.setStatusBarMessage(
                method === "dm/tickFile" ? "DM: added to the .dme" : "DM: removed from the .dme",
                3000
            );
        })
    );

    const tree = new ObjectTreeProvider();
    context.subscriptions.push(vscode.window.registerTreeDataProvider("dmObjectTree", tree));
    context.subscriptions.push(
        vscode.commands.registerCommand("dm.refreshObjectTree", () => tree.refresh())
    );

    // Open a type's declaration from the panel. `#name` is the server's type filter, so this asks
    // for types only rather than sifting a mixed list; the path match then picks the exact node,
    // since two branches can legitimately share a leaf name.
    context.subscriptions.push(
        vscode.commands.registerCommand("dm.revealType", async (node) => {
            const hits = await vscode.commands.executeCommand(
                "vscode.executeWorkspaceSymbolProvider",
                `#${node.name}`
            );

            const match = (hits || []).find((h) => h.containerName === node.path)
                || (hits || [])[0];

            if (!match) {
                vscode.window.showInformationMessage(`No declaration found for ${node.path}.`);
                return;
            }

            await vscode.window.showTextDocument(match.location.uri, {
                selection: match.location.range,
            });
        })
    );

    // The tree is built lazily on the first query that needs it, so the panel is empty until
    // something has asked. Refresh once the server is up, and again on save, since a saved edit
    // is when the tree most likely changed shape.
    client.onReady?.().then(() => tree.refresh(), () => {});
    context.subscriptions.push(vscode.workspace.onDidSaveTextDocument(() => tree.refresh()));
}

function deactivate() {
    return client ? client.stop() : undefined;
}

module.exports = { activate, deactivate };
