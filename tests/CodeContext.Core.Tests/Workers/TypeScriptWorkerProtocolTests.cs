using CodeContext.Core.Services;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;
using System.Text.Json;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// Phase 4 exit-gate fixtures for the persistent TypeScript worker. Requires Node.js
/// on PATH and an npm-installed src/CodeContext.TypeScript.Worker, hence quarantined
/// as external tooling (see DEVELOPMENT.md).
/// </summary>
[Trait("Category", "ExternalTooling")]
public class TypeScriptWorkerProtocolTests : IAsyncLifetime
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("cc-ts-worker-").FullName;
    private CSharpWorkerPipeline _pipeline = null!;

    public Task InitializeAsync()
    {
        _pipeline = new CSharpWorkerPipeline(_tempDir, TypeScriptWorkerRegistration());
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _pipeline.DisposeAsync();
        Directory.Delete(_tempDir, recursive: true);
    }

    public static RegisteredWorker TypeScriptWorkerRegistration()
    {
        // The worker runs from the source tree (script + node_modules live there).
        var workerDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "CodeContext.TypeScript.Worker"));
        var script = Path.Combine(workerDir, "typescript-worker.js");
        if (!File.Exists(script))
        {
            throw new FileNotFoundException($"TypeScript worker script not found at {script}.");
        }

        var manifest = new WorkerManifest(
            ManifestVersion: 1,
            ParserId: "typescript",
            DisplayName: "TypeScript",
            Version: "1.0.0",
            Command: "node",
            Args: ["typescript-worker.js"],
            MinProtocolVersion: ParserProtocol.Version,
            MaxProtocolVersion: ParserProtocol.Version,
            Languages: ["typescript", "javascript"],
            Extensions: [".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs", ".vue"],
            ProjectMarkers: ["tsconfig.json", "package.json"]);
        var spec = new WorkerLaunchSpec(
            "typescript", "TypeScript", "node", ["typescript-worker.js"], WorkingDirectory: workerDir);
        return new RegisteredWorker(manifest, spec, Path.Combine(workerDir, "worker-manifest.json"));
    }

    private async Task<string> WriteFileAsync(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    [Theory]
    [InlineData("module.mts")]
    [InlineData("module.cts")]
    [InlineData("module.mjs")]
    [InlineData("module.cjs")]
    public async Task CompilerNativeModuleExtension_IsIndexed(string fileName)
    {
        await WriteFileAsync(fileName, "export class NativeModule {}\n");

        await _pipeline.GraphUpdateService.PerformInitialScanAsync(
            _tempDir, null, CancellationToken.None);

        var nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        Assert.Contains(nodes, node =>
            node.FilePath?.EndsWith(fileName, StringComparison.OrdinalIgnoreCase) == true
            && node.Name == "NativeModule");
    }

    [Fact]
    public async Task VueScriptAndScriptSetup_AreIndexedAtOriginalSourceLocations()
    {
        await WriteFileAsync("Component.vue", """
            <template>
              <button @click="increment">{{ count }}</button>
            </template>
            <script setup lang="ts">
            const count: number = 0;
            function increment(): number { return count + 1; }
            </script>
            <style scoped>button { color: red; }</style>
            """);

        await _pipeline.GraphUpdateService.PerformInitialScanAsync(
            _tempDir, null, CancellationToken.None);

        var nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        var increment = Assert.Single(nodes, node =>
            node.FilePath?.EndsWith("Component.vue", StringComparison.OrdinalIgnoreCase) == true
            && node.Name == "increment");
        // Protocol spans are normalized to the host's zero-based representation.
        Assert.Equal(5, increment.StartLine);
        Assert.DoesNotContain(nodes, node => node.Name is "button" or "style");
    }

    [Fact]
    public async Task VueImports_ParticipateInCrossFileSemanticRelationships()
    {
        await WriteFileAsync("Base.vue", """
            <script lang="ts">
            export class Base {
              greet(): string { return 'hello'; }
            }
            </script>
            """);
        await WriteFileAsync("Derived.vue", """
            <template><div /></template>
            <script lang="ts">
            import { Base } from './Base.vue';
            export class Derived extends Base {
              run(): string { return this.greet(); }
            }
            </script>
            """);

        await _pipeline.GraphUpdateService.PerformInitialScanAsync(
            _tempDir, null, CancellationToken.None);

        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        Assert.Contains(edges, edge =>
            edge.Type == "EXTENDS"
            && edge.SourceId == "typescript:default:Derived.vue#Derived"
            && edge.TargetId == "typescript:default:Base.vue#Base");
        Assert.Contains(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:Derived.vue#Derived.run()"
            && edge.TargetId == "typescript:default:Base.vue#Base.greet()");
    }

    [Fact]
    public async Task VueScriptEdit_ReplacesEmbeddedFactsIncrementally()
    {
        var component = await WriteFileAsync("Editable.vue", """
            <template><div /></template>
            <script lang="ts">export class Before {}</script>
            """);
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(
            _tempDir, null, CancellationToken.None);

        await File.WriteAllTextAsync(component, """
            <template><div /></template>
            <script lang="ts">export class After {}</script>
            """);
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(
            component, FileChangeType.Changed, CancellationToken.None);

        var nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        Assert.Contains(nodes, node => node.Id == "typescript:default:Editable.vue#After");
        Assert.DoesNotContain(nodes, node => node.Id == "typescript:default:Editable.vue#Before");
    }

    [Fact]
    public async Task MalformedVueSfc_ReportsFrameworkDiagnostic()
    {
        await using var pipeline = RecordingPipeline(out var recorder);
        await WriteFileAsync("Broken.vue", """
            <template><div></template>
            <script setup lang="ts">const value = 1;</script>
            """);

        await pipeline.GraphUpdateService.PerformInitialScanAsync(
            _tempDir, null, CancellationToken.None);

        Assert.Contains(
            recorder.Deltas.SelectMany(delta => delta.Diagnostics ?? []),
            diagnostic => diagnostic.Message.Contains("Vue SFC parse error", StringComparison.Ordinal));
    }

    private async Task<IReadOnlyList<CodeEdge>> ScanEdgesAsync()
    {
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(
            _tempDir, null, CancellationToken.None);
        return [.. await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync()];
    }

    [Fact]
    public async Task VueTemplateComponentTag_RecordsARenderOfTheImportedComponent()
    {
        // The child has no <script> at all: it is still a component, and the file is
        // still the import's target.
        await WriteFileAsync("Child.vue", """
            <template><p>child</p></template>
            """);
        await WriteFileAsync("Parent.vue", """
            <template>
              <div>
                <Child v-if="visible" />
              </div>
            </template>
            <script setup>
            import Child from './Child.vue';
            const visible = true;
            </script>
            """);

        var edges = await ScanEdgesAsync();

        Assert.Contains(edges, edge =>
            edge.Type == "IMPORTS"
            && edge.SourceId == "typescript:default:Parent.vue"
            && edge.TargetId == "typescript:default:Child.vue");
        var render = Assert.Single(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:Parent.vue"
            && edge.TargetId == "typescript:default:Child.vue");
        // Edge lines are normalized to the host's zero-based representation, like spans.
        Assert.Equal("2", render.Metadata!["line"]);
        Assert.Equal("Child", render.Metadata["callTarget"]);
    }

    [Fact]
    public async Task VueTemplateComponentTag_ResolvesKebabCaseAndOptionsApiRegistration()
    {
        await WriteFileAsync("ChildCard.vue", """
            <template><p>child</p></template>
            <script>
            export default { name: 'ChildCard' };
            </script>
            """);
        await WriteFileAsync("Parent.vue", """
            <template>
              <child-card />
            </template>
            <script>
            import ChildCard from './ChildCard.vue';
            export default { components: { ChildCard } };
            </script>
            """);

        var edges = await ScanEdgesAsync();

        Assert.Contains(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:Parent.vue"
            && edge.TargetId == "typescript:default:ChildCard.vue"
            && edge.Metadata?["callTarget"] == "child-card");
    }

    [Theory]
    [InlineData(".nuxt/components.d.ts", "../")]
    [InlineData(".nuxt/types/components.d.ts", "../../")]
    [InlineData("components.d.ts", "./")]
    public async Task VueTemplateComponentTag_ResolvesAComponentTheProjectDeclaresGlobally(
        string declarations, string toRoot)
    {
        // What Nuxt and unplugin-vue-components generate for components no script imports.
        await WriteFileAsync(declarations, $$"""
            type LazyComponent<T> = T
            interface _GlobalComponents {
              'BaseButton': typeof import("{{toRoot}}components/base/Button.vue")['default']
              'LazyBaseButton': LazyComponent<typeof import("{{toRoot}}components/base/Button.vue")['default']>
              'Shadowed': typeof import("{{toRoot}}components/base/Button.vue")['default']
              'NuxtLink': typeof import("{{toRoot}}node_modules/nuxt/dist/app/components/nuxt-link")['default']
            }
            declare module 'vue' {
              export interface GlobalComponents extends _GlobalComponents { }
            }
            export {}
            """);
        await WriteFileAsync("components/base/Button.vue", "<template><button /></template>\n");
        await WriteFileAsync("components/Local.vue", "<template><i /></template>\n");
        await WriteFileAsync("pages/index.vue", """
            <template>
              <NuxtLink><BaseButton /></NuxtLink>
            </template>
            """);
        await WriteFileAsync("pages/deep/about.vue", """
            <template>
              <lazy-base-button />
              <Shadowed />
            </template>
            <script setup>
            import Shadowed from '../../components/Local.vue';
            </script>
            """);

        var edges = await ScanEdgesAsync();

        string[] Rendered(string file) => [.. edges
            .Where(edge => edge.Type == "CALLS" && edge.SourceId == $"typescript:default:{file}")
            .Select(edge => $"{edge.Metadata!["callTarget"]} -> {edge.TargetId!.Replace("typescript:default:", "")}")
            .Order(StringComparer.Ordinal)];
        // A component outside the project claims nothing, and a name the script binds is
        // the script's.
        Assert.Equal(["BaseButton -> components/base/Button.vue"], Rendered("pages/index.vue"));
        Assert.Equal(
            ["Shadowed -> components/Local.vue", "lazy-base-button -> components/base/Button.vue"],
            Rendered("pages/deep/about.vue"));
    }

    [Fact]
    public async Task VueTemplateComponentTag_ResolvesAComponentRegisteredOnTheApplication()
    {
        await WriteFileAsync("src/Card.vue", "<template><p /></template>\n");
        await WriteFileAsync("src/Badge.vue", "<template><b /></template>\n");
        await WriteFileAsync("src/main.ts", """
            import { createApp } from 'vue';
            import Card from './Card.vue';
            import Badge from './Badge.vue';
            import App from './App.vue';
            const app = createApp(App);
            app.component('AppCard', Card);
            app.component('status-badge', Badge);
            app.mount('#app');
            """);
        await WriteFileAsync("src/App.vue", """
            <template>
              <app-card><StatusBadge /><Unknown /></app-card>
            </template>
            """);

        var edges = await ScanEdgesAsync();

        string[] rendered = [.. edges
            .Where(edge => edge.Type == "CALLS" && edge.SourceId == "typescript:default:src/App.vue")
            .Select(edge => $"{edge.Metadata!["callTarget"]} -> {edge.TargetId!.Replace("typescript:default:", "")}")
            .Order(StringComparer.Ordinal)];
        // Vue finds a tag under its own, camel-case and Pascal-case spellings, so a
        // Pascal-case tag does not find a kebab-case registration.
        Assert.Equal(["app-card -> src/Card.vue"], rendered);
    }

    [Fact]
    public async Task NuxtProjectWithoutGeneratedDeclarations_SaysWhatTemplatesWillMiss()
    {
        await using var pipeline = RecordingPipeline(out var recorder);
        await WriteFileAsync("nuxt.config.ts", "export default {};\n");
        await WriteFileAsync("pages/index.vue", "<template><BaseButton /></template>\n");

        await pipeline.GraphUpdateService.PerformInitialScanAsync(
            _tempDir, null, CancellationToken.None);

        Assert.Contains(
            recorder.Deltas.SelectMany(delta => delta.Diagnostics ?? []),
            diagnostic => diagnostic.Message.Contains(".nuxt/components.d.ts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VueTemplateComponentTag_ResolvesAComponentDeclaredInTypeScript()
    {
        await WriteFileAsync("widgets.ts", """
            export function Badge() { return null; }
            """);
        await WriteFileAsync("Parent.vue", """
            <template>
              <Badge />
              <div />
              <Unknown />
            </template>
            <script setup lang="ts">
            import { Badge } from './widgets';
            </script>
            """);

        var edges = await ScanEdgesAsync();

        Assert.Contains(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:Parent.vue"
            && edge.TargetId == "typescript:default:widgets.ts#Badge()");
        // A tag that binds to nothing in the script (a native element, or a globally
        // registered component) claims no target.
        Assert.DoesNotContain(edges, edge =>
            edge.SourceId == "typescript:default:Parent.vue"
            && edge.Metadata?.GetValueOrDefault("callTarget") is "div" or "Unknown");
    }

    [Fact]
    public async Task VueTemplateExpressions_RecordCallsToScriptFunctionsAtTheirTemplateLines()
    {
        await WriteFileAsync("Panel.vue", """
            <template>
              <ul :class="{ wide: isWide(), tall }" @click="select(1); announce()">
                <li v-for="item in visibleItems()" :key="item.id">{{ format(item) }}</li>
              </ul>
            </template>
            <script setup>
            const tall = true;
            const isWide = () => true;
            const select = (id) => id;
            const announce = () => {};
            const visibleItems = () => [];
            function format(item) { return String(item); }
            </script>
            """);

        var edges = await ScanEdgesAsync();

        (string Target, string Line)[] expected =
        [
            ("isWide", "1"), ("select", "1"), ("announce", "1"), ("visibleItems", "2"), ("format(item)", "2"),
        ];
        foreach (var (target, line) in expected)
        {
            var call = Assert.Single(edges, edge =>
                edge.Type == "CALLS"
                && edge.SourceId == "typescript:default:Panel.vue"
                && edge.TargetId == $"typescript:default:Panel.vue#{target}");
            Assert.Equal(line, call.Metadata!["line"]);
        }
    }

    [Fact]
    public async Task VueTemplateNames_RecordANamedEventHandlerAndADynamicComponent()
    {
        await WriteFileAsync("Panel.vue", """
            <template><p>panel</p></template>
            """);
        await WriteFileAsync("Host.vue", """
            <template>
              <button @click="save">Save</button>
              <component :is="Panel" />
            </template>
            <script setup>
            import Panel from './Panel.vue';
            const count = 0;
            const save = () => count;
            </script>
            """);

        var edges = await ScanEdgesAsync();

        // `@click="save"` names the function Vue calls; no call expression is written.
        Assert.Contains(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:Host.vue"
            && edge.TargetId == "typescript:default:Host.vue#save"
            && edge.Metadata?["line"] == "1");
        Assert.Contains(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:Host.vue"
            && edge.TargetId == "typescript:default:Panel.vue");
    }

    [Fact]
    public async Task VueTemplateExpressionThatDoesNotParse_IsLeftOutWithoutLosingTheScript()
    {
        // An unterminated template literal copied beside the script would swallow it.
        await WriteFileAsync("Broken.vue", """
            <template>
              <p :title="`unterminated" :class="label(">{{ fine() }}</p>
            </template>
            <script setup>
            const fine = () => 1;
            function label() { return ''; }
            </script>
            """);

        await _pipeline.GraphUpdateService.PerformInitialScanAsync(
            _tempDir, null, CancellationToken.None);

        var nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        Assert.Contains(nodes, node => node.Id == "typescript:default:Broken.vue#fine");
        Assert.Contains(nodes, node => node.Id == "typescript:default:Broken.vue#label()");
        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        Assert.Contains(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:Broken.vue"
            && edge.TargetId == "typescript:default:Broken.vue#fine");
        Assert.DoesNotContain(edges, edge => edge.TargetId == "typescript:default:Broken.vue#label()");
    }

    [Fact]
    public async Task VueTemplateEdit_ReplacesTemplateUsageIncrementally()
    {
        await WriteFileAsync("Child.vue", """
            <template><p>child</p></template>
            """);
        var parent = await WriteFileAsync("Parent.vue", """
            <template><Child /></template>
            <script setup>
            import Child from './Child.vue';
            const refresh = () => 1;
            </script>
            """);
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(
            _tempDir, null, CancellationToken.None);

        await File.WriteAllTextAsync(parent, """
            <template><p>{{ refresh() }}</p></template>
            <script setup>
            import Child from './Child.vue';
            const refresh = () => 1;
            </script>
            """);
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(
            parent, FileChangeType.Changed, CancellationToken.None);

        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        Assert.DoesNotContain(edges, edge =>
            edge.Type == "CALLS" && edge.TargetId == "typescript:default:Child.vue");
        Assert.Contains(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:Parent.vue"
            && edge.TargetId == "typescript:default:Parent.vue#refresh");
    }

    [Fact]
    public async Task VueScriptsWithoutImports_DoNotShareTheirNamesAcrossComponents()
    {
        // A script with no import or export is a global script to the compiler, where
        // same-named declarations in two files merge. A Vue script is always a module.
        foreach (var name in new[] { "First", "Second" })
        {
            await WriteFileAsync($"{name}.vue", """
                <template>
                  <button @click="close">{{ label() }}</button>
                </template>
                <script setup>
                function close() { return label(); }
                function label() { return ''; }
                </script>
                """);
        }

        var edges = await ScanEdgesAsync();

        var calls = edges.Where(edge => edge.Type == "CALLS" && edge.Metadata?.ContainsKey("unresolved") != true);
        Assert.All(calls, edge => Assert.Equal(
            edge.SourceId!.Split('#')[0], edge.TargetId!.Split('#')[0]));
        foreach (var name in new[] { "First", "Second" })
        {
            Assert.Contains(calls, edge =>
                edge.SourceId == $"typescript:default:{name}.vue"
                && edge.TargetId == $"typescript:default:{name}.vue#close()");
            Assert.Contains(calls, edge =>
                edge.SourceId == $"typescript:default:{name}.vue"
                && edge.TargetId == $"typescript:default:{name}.vue#label()");
            Assert.Contains(calls, edge =>
                edge.SourceId == $"typescript:default:{name}.vue#close()"
                && edge.TargetId == $"typescript:default:{name}.vue#label()");
        }
    }

    [Fact]
    public async Task VueTemplateNamesDeclaredByVForOrVSlot_DoNotBindToTheScript()
    {
        await WriteFileAsync("Slotted.vue", """
            <template><slot :remove="() => 1" /></template>
            """);
        await WriteFileAsync("List.vue", """
            <template>
              <ul>
                <li v-for="(save, format) in rows()" :key="format" @click="save" :title="save(format)">
                  {{ format() }}<i :title="keep(format)" :class="[save]" />
                </li>
              </ul>
              <Slotted v-slot="{ remove }"><b @click="remove(1)">{{ keep() }}</b></Slotted>
            </template>
            <script setup>
            import Slotted from './Slotted.vue';
            const rows = () => [];
            const save = () => 1;
            const format = () => '';
            const remove = () => 1;
            const keep = () => 1;
            </script>
            """);

        var edges = await ScanEdgesAsync();

        string[] Targets() => [.. edges
            .Where(edge => edge.Type == "CALLS" && edge.SourceId == "typescript:default:List.vue")
            .Select(edge => edge.TargetId!.Replace("typescript:default:", ""))
            .Order(StringComparer.Ordinal)];
        // The loop's source is read before its names exist; a name it or the slot
        // declares is the loop's or the slot's, whatever the script calls its own.
        Assert.Equal(["List.vue#keep", "List.vue#keep", "List.vue#rows", "Slotted.vue"], Targets());
        // Nor is such a name the script's function when it is passed along.
        Assert.DoesNotContain(edges, edge => edge.Type == "REFERENCES");
    }

    [Fact]
    public async Task VueTemplateExpressionThatDeclares_IsLeftOutRatherThanMintingNodes()
    {
        await WriteFileAsync("Declares.vue", """
            <template>
              <b :options="{ save() { return go() } }" @click="() => { const helper = () => go(); helper() }">
                {{ go() }}
              </b>
            </template>
            <script setup>
            const go = () => 1;
            const helper = () => 2;
            </script>
            """);

        await _pipeline.GraphUpdateService.PerformInitialScanAsync(
            _tempDir, null, CancellationToken.None);

        var nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        Assert.Equal(
            ["Declares.vue", "go", "helper"],
            nodes.Select(node => node.Name).Order(StringComparer.Ordinal));
        // Zero-based: the script's `helper`, not the template's, on physical line 8.
        Assert.Equal(7, Assert.Single(nodes, node => node.Name == "helper").StartLine);
        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        Assert.Single(edges, edge =>
            edge.Type == "CALLS" && edge.TargetId == "typescript:default:Declares.vue#go");
    }

    [Fact]
    public async Task ANameBoundToALocalValue_IsNotARenderOfItsOwnModule()
    {
        await WriteFileAsync("Dynamic.vue", """
            <template><component :is="current" /></template>
            <script setup>
            import { ref } from 'vue';
            const current = ref(null);
            </script>
            """);
        await WriteFileAsync("Layout.tsx", """
            export function Layout({ Header, as }: { Header: () => null; as?: string }) {
              const Tag = as || 'div';
              return <Tag><Header /></Tag>;
            }
            """);

        var edges = await ScanEdgesAsync();

        Assert.DoesNotContain(edges, edge =>
            edge.Type == "CALLS" && edge.Metadata?.ContainsKey("unresolved") != true);
    }

    [Fact]
    public async Task VueTemplateAfterTheScript_WithWindowsLineEndings_KeepsEveryLine()
    {
        await WriteFileAsync("Tail.vue", string.Join("\r\n",
            "<script setup lang=\"ts\">",
            "const first = (): number => 1;",
            "function second(): number { return first(); }",
            "</script>",
            "<template>",
            "  <p :title=\"String(second())\">{{ first() }}{{ second() }}</p>",
            "</template>",
            ""));

        await _pipeline.GraphUpdateService.PerformInitialScanAsync(
            _tempDir, null, CancellationToken.None);

        var nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        Assert.Equal(1, Assert.Single(nodes, node => node.Name == "first").StartLine);
        Assert.Equal(2, Assert.Single(nodes, node => node.Name == "second").StartLine);
        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        var fromTemplate = edges
            .Where(edge => edge.Type == "CALLS" && edge.SourceId == "typescript:default:Tail.vue"
                && edge.Metadata?.ContainsKey("unresolved") != true)
            .Select(edge => (edge.TargetId, Line: edge.Metadata!["line"]))
            .Order();
        Assert.Equal(
            [
                ("typescript:default:Tail.vue#first", "5"),
                ("typescript:default:Tail.vue#second()", "5"),
                ("typescript:default:Tail.vue#second()", "5"),
            ],
            fromTemplate);
    }

    [Fact]
    public async Task VueTemplate_DoesNotDisturbScriptFactsOrMintNodes()
    {
        await WriteFileAsync("Quiet.vue", """
            <template>
              <button :style="{ color }" @click="() => bump()">{{ label }}</button>
              <p v-for="(row, index) in rows" :key="index">{{ row }}</p>
            </template>
            <script setup lang="ts">
            const color = 'red';
            const label = 'go';
            const rows: string[] = [];
            function bump(): void {}
            </script>
            """);

        await _pipeline.GraphUpdateService.PerformInitialScanAsync(
            _tempDir, null, CancellationToken.None);

        var nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        var names = nodes
            .Where(node => node.FilePath?.EndsWith("Quiet.vue", StringComparison.OrdinalIgnoreCase) == true)
            .Select(node => node.Name)
            .Order(StringComparer.Ordinal);
        Assert.Equal(["Quiet.vue", "bump"], names);
        var bump = Assert.Single(nodes, node => node.Name == "bump");
        Assert.Equal(8, bump.StartLine);
    }

    [Fact]
    public async Task VueAliasImport_ResolvesThroughTheTsconfigNuxtGenerates()
    {
        // A JavaScript Nuxt project need not have a root tsconfig.json; the aliases it
        // builds with are declared in the one Nuxt writes under .nuxt/.
        await WriteFileAsync(".nuxt/tsconfig.json", """
            { "compilerOptions": { "paths": { "~/*": ["../*"], "~": [".."] } } }
            """);
        await WriteFileAsync("components/DownloadQueue.vue", """
            <template><p>queue</p></template>
            """);
        await WriteFileAsync("server/queue.js", """
            export function getQueue() { return []; }
            """);
        await WriteFileAsync("pages/index.vue", """
            <template>
              <DownloadQueue :queue="getQueue()" />
            </template>
            <script setup>
            import DownloadQueue from '~/components/DownloadQueue.vue';
            import { getQueue } from '~/server/queue';
            </script>
            """);

        var edges = await ScanEdgesAsync();

        Assert.Contains(edges, edge =>
            edge.Type == "IMPORTS"
            && edge.SourceId == "typescript:default:pages/index.vue"
            && edge.TargetId == "typescript:default:components/DownloadQueue.vue");
        Assert.Contains(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:pages/index.vue"
            && edge.TargetId == "typescript:default:components/DownloadQueue.vue");
        Assert.Contains(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:pages/index.vue"
            && edge.TargetId == "typescript:default:server/queue.js#getQueue()");
    }

    [Fact]
    public async Task VueAliasImport_ResolvesPathsInheritedFromAnExtendedConfig()
    {
        // Paths declared without baseUrl are relative to the config that declares them,
        // not to the root config that extends it.
        await WriteFileAsync("config/base.json", """
            { "compilerOptions": { "paths": { "#ui/*": ["../ui/*"] } } }
            """);
        await WriteFileAsync("tsconfig.json", """
            { "extends": "./config/base.json" }
            """);
        await WriteFileAsync("ui/Chip.vue", """
            <template><i /></template>
            """);
        await WriteFileAsync("App.vue", """
            <template><Chip /></template>
            <script setup lang="ts">
            import Chip from '#ui/Chip.vue';
            </script>
            """);

        var edges = await ScanEdgesAsync();

        Assert.Contains(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:App.vue"
            && edge.TargetId == "typescript:default:ui/Chip.vue");
    }

    [Fact]
    public async Task SolutionStyleTsconfig_ResolvesAnAliasDeclaredByAReferencedProject()
    {
        await WriteFileAsync("tsconfig.json", """
            { "files": [], "references": [{ "path": "./tsconfig.app.json" }, { "path": "./tools" }] }
            """);
        await WriteFileAsync("tsconfig.app.json", """
            { "compilerOptions": { "paths": { "@/*": ["./src/*"] } }, "include": ["src"] }
            """);
        await WriteFileAsync("tools/tsconfig.json", """
            { "compilerOptions": { "paths": { "@tools/*": ["./*"] } } }
            """);
        await WriteFileAsync("tools/emit.ts", "export function emit(): void {}\n");
        await WriteFileAsync("src/util.ts", "export function helper(): number { return 1; }\n");
        await WriteFileAsync("src/main.ts", """
            import { helper } from '@/util';
            import { emit } from '@tools/emit';
            export function main(): number { emit(); return helper(); }
            """);

        var edges = await ScanEdgesAsync();

        Assert.Contains(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:src/main.ts#main()"
            && edge.TargetId == "typescript:default:src/util.ts#helper()");
        Assert.Contains(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:src/main.ts#main()"
            && edge.TargetId == "typescript:default:tools/emit.ts#emit()");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AliasTwoProjectsDeclare_ResolvesInTheProjectOfTheImportingFile(bool rootReferencesThem)
    {
        if (rootReferencesThem)
        {
            await WriteFileAsync("tsconfig.json", """
                { "files": [], "references": [{ "path": "./apps/web" }, { "path": "./apps/admin" }] }
                """);
        }

        foreach (var app in new[] { "web", "admin" })
        {
            // One of the two declares its aliases in jsconfig.json, as a JavaScript project does.
            await WriteFileAsync($"apps/{app}/{(app == "web" ? "tsconfig" : "jsconfig")}.json", """
                { "compilerOptions": { "paths": { "@/*": ["./src/*"] } } }
                """);
            await WriteFileAsync($"apps/{app}/src/util.ts", "export function helper(): number { return 1; }\n");
            await WriteFileAsync($"apps/{app}/src/Panel.vue", "<template><p /></template>\n");
            await WriteFileAsync($"apps/{app}/src/deep/main.ts", """
                import { helper } from '@/util';
                import Panel from '@/Panel.vue';
                export function main(): number { return helper(); }
                """);
        }

        var edges = await ScanEdgesAsync();

        foreach (var app in new[] { "web", "admin" })
        {
            Assert.Single(edges, edge =>
                edge.Type == "CALLS"
                && edge.SourceId == $"typescript:default:apps/{app}/src/deep/main.ts#main()"
                && edge.TargetId == $"typescript:default:apps/{app}/src/util.ts#helper()");
            Assert.Single(edges, edge =>
                edge.Type == "IMPORTS"
                && edge.SourceId == $"typescript:default:apps/{app}/src/deep/main.ts"
                && edge.TargetId == $"typescript:default:apps/{app}/src/Panel.vue");
        }
    }

    [Fact]
    public async Task Jsconfig_SuppliesAliasesWhenThereIsNoTsconfig()
    {
        await WriteFileAsync("jsconfig.json", """
            { "compilerOptions": { "paths": { "@/*": ["./src/*"] } } }
            """);
        await WriteFileAsync("src/util.js", "export function helper() { return 1; }\n");
        await WriteFileAsync("src/main.js", """
            import { helper } from '@/util';
            export function main() { return helper(); }
            """);

        var edges = await ScanEdgesAsync();

        Assert.Contains(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:src/main.js#main()"
            && edge.TargetId == "typescript:default:src/util.js#helper()");
    }

    [Fact]
    public async Task SideEffectImportOfAScriptFile_ResolvesToItsModule()
    {
        // A file with no import or export is a script to the compiler, so the import
        // has no module symbol to bind; the file it names is still its target.
        await WriteFileAsync("polyfill.js", "globalThis.ready = true;\n");
        await WriteFileAsync("entry.js", "import './polyfill.js';\n");

        var edges = await ScanEdgesAsync();

        Assert.Contains(edges, edge =>
            edge.Type == "IMPORTS"
            && edge.SourceId == "typescript:default:entry.js"
            && edge.TargetId == "typescript:default:polyfill.js");
    }

    [Fact]
    public async Task JsxElement_RecordsARenderOfTheComponent()
    {
        await WriteFileAsync("Button.tsx", """
            export function Button() { return <button />; }
            export const Icon = () => <i />;
            """);
        await WriteFileAsync("App.tsx", """
            import { Button, Icon } from './Button';
            export function App() {
              return <main><Button><Icon /></Button></main>;
            }
            """);

        var edges = await ScanEdgesAsync();

        Assert.Single(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:App.tsx#App()"
            && edge.TargetId == "typescript:default:Button.tsx#Button()");
        Assert.Single(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:App.tsx#App()"
            && edge.TargetId == "typescript:default:Button.tsx#Icon");
        // Intrinsic elements are not project symbols and claim nothing.
        Assert.DoesNotContain(edges, edge =>
            edge.Metadata?.GetValueOrDefault("callTarget") is "main" or "button" or "i");
    }

    [Fact]
    public async Task FunctionPassedAsAValue_IsAReferenceFromWhereItIsPassed()
    {
        await WriteFileAsync("helpers.ts", """
            export function format(x: number) { return String(x); }
            export const save = () => {};
            export function first() {}
            export function second() {}
            export function shorthand() {}
            export function onlyCalled() {}
            export class Store {
              static load() {}
              flush() {}
            }
            """);
        await WriteFileAsync("App.tsx", """
            import { format, save, first, second, shorthand, onlyCalled, Store } from './helpers';
            export function list(items: number[]) {
              onlyCalled();
              return items.map(format);
            }
            export function View() {
              return <button onClick={save} onFocus={Store.load} />;
            }
            export function table(store: Store) {
              return { run: first, shorthand, all: [second, store.flush] };
            }
            export function shadowed(format: (x: number) => string, items: number[]) {
              return items.map(format);
            }
            """);

        var edges = await ScanEdgesAsync();

        void Referenced(string source, string target) => Assert.Single(edges, edge =>
            edge.Type == "REFERENCES"
            && edge.SourceId == $"typescript:default:App.tsx#{source}"
            && edge.TargetId == $"typescript:default:helpers.ts#{target}");

        Referenced("list(number[])", "format(number)");
        Referenced("View()", "save");
        Referenced("View()", "Store.load()");
        Referenced("table(Store)", "first()");
        Referenced("table(Store)", "shorthand()");
        Referenced("table(Store)", "second()");
        Referenced("table(Store)", "Store.flush()");

        // A call is a call and nothing more; a parameter that hides the import is not it.
        Assert.DoesNotContain(edges, edge =>
            edge.Type == "REFERENCES" && edge.TargetId!.EndsWith("#onlyCalled()", StringComparison.Ordinal));
        Assert.DoesNotContain(edges, edge =>
            edge.Type == "REFERENCES" && edge.SourceId!.Contains("#shadowed(", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InitialScan_ReportsTypeScriptFileProgressBeforeCompletion()
    {
        await WriteFileAsync("one.ts", "export class One {}\n");
        await WriteFileAsync("two.ts", "export class Two {}\n");
        var reporter = new RecordingProgressReporter();

        await _pipeline.GraphUpdateService.PerformInitialScanAsync(
            _tempDir, reporter, CancellationToken.None);

        Assert.Contains((1, 2), reporter.Progress);
        Assert.Equal((2, 2), reporter.Progress[^1]);
    }

    [Fact]
    public async Task MixedCSharpAndTypeScriptFixture_ReachesReadyWithBothLanguagesIndexed()
    {
        await WriteFileAsync("Service.cs", @"
namespace Mixed { public class Service { public string Run() => ""ok""; } }");
        await WriteFileAsync("client.ts",
            "export class Client { fetch(): string { return 'GET /api/service'; } }\n");

        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        Assert.Contains(nodes, n => n.Language == "csharp" && n.Name == "Service");
        Assert.Contains(nodes, n => n.Language == "typescript" && n.Name == "Client");

        var sessions = _pipeline.SessionRegistry.GetSnapshots();
        Assert.Equal(ParserSessionState.Ready, sessions.Single(s => s.ParserId == "csharp").State);
        Assert.Equal(ParserSessionState.Ready, sessions.Single(s => s.ParserId == "typescript").State);
    }

    private sealed class RecordingProgressReporter : IScanProgressReporter
    {
        public List<(int Processed, int Total)> Progress { get; } = [];

        public void ReportProgress(int processed, int total, string currentFile)
            => Progress.Add((processed, total));

        public void ReportComplete(int totalProcessed, TimeSpan elapsed) { }
        public void ReportError(string filePath, string error) { }
    }

    [Fact]
    public async Task CrossFileTypeScriptRelationships_ResolveWithProjectSemantics()
    {
        await WriteFileAsync("base.ts",
            "export class Base { greet(): string { return 'hi'; } }\n");
        await WriteFileAsync("derived.ts",
            "import { Base } from './base';\n" +
            "export class Derived extends Base { run(): string { return this.greet(); } }\n");

        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();

        // The EXTENDS target is the *resolved* node in the other file, not a bare name:
        // that's the project-semantics half of the Phase 4 exit gate.
        Assert.Contains(edges, e =>
            e.Type == "EXTENDS"
            && e.SourceId == "typescript:default:derived.ts#Derived"
            && e.TargetId == "typescript:default:base.ts#Base");
        Assert.Contains(edges, e =>
            e.Type == "CALLS"
            && e.SourceId == "typescript:default:derived.ts#Derived.run()"
            && e.TargetId == "typescript:default:base.ts#Base.greet()");
    }

    [Fact]
    public async Task ExplicitHeritageEmitsMemberFamiliesButStructuralCompatibilityDoesNot()
    {
        await WriteFileAsync("family.ts", """
            export interface IService { run(): void; }
            export class Implementation implements IService { run(): void { } }
            export class StructuralOnly { run(): void { } }
            """);
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        Assert.All(nodes.Where(node => node.Language == "typescript"),
            node => Assert.False(string.IsNullOrWhiteSpace(node.Identifier)));
        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        var memberEdge = Assert.Single(edges, edge => edge.Type == "IMPLEMENTS_MEMBER");
        Assert.Contains("#Implementation.run()", memberEdge.SourceId);
        Assert.Contains("#IService.run()", memberEdge.TargetId);
        Assert.DoesNotContain(edges, edge => edge.Type == "IMPLEMENTS_MEMBER"
            && edge.SourceId!.Contains("StructuralOnly", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TopLevelInvocation_HasARealModuleScopeCaller()
    {
        await WriteFileAsync("entry.ts",
            "function startReadLoop(): void { }\nstartReadLoop();\n");

        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        Assert.Contains(nodes, node => node.Id == "typescript:default:entry.ts" && node.Type == "Module");
        Assert.Contains(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:entry.ts"
            && edge.TargetId == "typescript:default:entry.ts#startReadLoop()");
    }

    [Fact]
    public async Task HocWrappedArrowVariable_AttributesNestedCallsToItsOwnNameNotFileScope()
    {
        // A handler wrapped by a single-level HOC (`guard(fn)`) has an initializer of
        // kind CallExpression, not ArrowFunction: naively that isn't recognized as an
        // enclosing declaration, so a call nested inside the wrapped arrow (here,
        // post()) fell through all the way to file scope instead of attributing to
        // `revoke`, and `revoke` itself was never queryable as a Function node.
        await WriteFileAsync("handlers.ts", """
            function guard(fn: () => Promise<void>): () => Promise<void> {
                return fn;
            }
            function post(): Promise<void> {
                return Promise.resolve();
            }

            const revoke = guard(async () => {
                await post();
            });
            """);

        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();

        Assert.Contains(nodes, node =>
            node.Id == "typescript:default:handlers.ts#revoke" && node.Type == "Function");
        Assert.Contains(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:handlers.ts#revoke"
            && edge.TargetId == "typescript:default:handlers.ts#post()");
        Assert.DoesNotContain(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:handlers.ts"
            && edge.TargetId == "typescript:default:handlers.ts#post()");
    }

    [Fact]
    public async Task HocWrappedArrowClassField_AttributesNestedCallsToItsOwnNameNotFileScope()
    {
        // Same HOC-wrapping gap, but for the common Express-controller shape: a route
        // handler held in a class field (`revoke = guard(async () => {...})`) rather
        // than a top-level const.
        await WriteFileAsync("controller.ts", """
            function guard(fn: () => Promise<void>): () => Promise<void> {
                return fn;
            }
            function post(): Promise<void> {
                return Promise.resolve();
            }

            export class KeysController {
                revoke = guard(async () => {
                    await post();
                });
            }
            """);

        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();

        Assert.Contains(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:controller.ts#KeysController.revoke"
            && edge.TargetId == "typescript:default:controller.ts#post()");
        Assert.DoesNotContain(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:controller.ts"
            && edge.TargetId == "typescript:default:controller.ts#post()");
    }

    [Fact]
    public async Task MethodChainEndingInAnArrow_IsNotMistakenForAHocWrappedHandler()
    {
        // Guards against a regression in the HOC-unwrap above: `.map(fn)` has a
        // CallExpression initializer whose last argument is an arrow, exactly like
        // `guard(fn)` does — but its callee is a PropertyAccessExpression, not a bare
        // name, and its result (an array) is a value, never itself callable. `ids`
        // must NOT become a phantom Function node, and the nested `lookup()` call
        // must NOT be attributed to it.
        await WriteFileAsync("mapped.ts", """
            function lookup(id: string): string {
                return id;
            }
            const users: string[] = [];

            const ids = users.map(u => {
                return lookup(u);
            });
            """);

        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();

        Assert.DoesNotContain(nodes, node =>
            node.Id == "typescript:default:mapped.ts#ids" && node.Type == "Function");
        Assert.DoesNotContain(edges, edge =>
            edge.Type == "CALLS" && edge.SourceId == "typescript:default:mapped.ts#ids");
    }

    [Fact]
    public async Task BarrelReExport_ProducesAnImportsEdgeLikeARegularImport()
    {
        await WriteFileAsync("origin.ts", "export function ping(): number { return 42; }\n");
        await WriteFileAsync("barrel.ts", "export * from './origin';\n");

        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        Assert.Contains(edges, edge =>
            edge.Type == "IMPORTS"
            && edge.SourceId == "typescript:default:barrel.ts"
            && edge.TargetId == "typescript:default:origin.ts");
    }

    [Fact]
    public async Task AnonymousDefaultExportedFunction_GetsARealNodeAndAttributesNestedCalls()
    {
        await WriteFileAsync("default-fn.ts", """
            function callee(): void { }

            export default function () {
                callee();
            }
            """);

        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        Assert.Contains(nodes, node =>
            node.Id == "typescript:default:default-fn.ts#default()" && node.Type == "Function");
        Assert.Contains(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:default-fn.ts#default()"
            && edge.TargetId == "typescript:default:default-fn.ts#callee()");
    }

    [Fact]
    public async Task AnonymousDefaultExportedArrow_GetsARealNodeAndAttributesNestedCalls()
    {
        await WriteFileAsync("default-arrow.ts", """
            function callee(): void { }

            export default () => {
                callee();
            };
            """);

        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        Assert.Contains(nodes, node =>
            node.Id == "typescript:default:default-arrow.ts#default" && node.Type == "Function");
        Assert.Contains(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:default-arrow.ts#default"
            && edge.TargetId == "typescript:default:default-arrow.ts#callee()");
    }

    [Fact]
    public async Task ExportEqualsArrow_IsNotMistakenForADefaultExportedArrow()
    {
        // `export = () => {...}` (TS's legacy CommonJS-style export) has the identical
        // ExportAssignment/arrow AST shape as `export default () => {...}`, but node
        // creation deliberately skips it (isExportEquals). Guards against reintroducing
        // the dangling-edge bug fixed above: attribution must NOT compute a "#default"
        // id for something no node was ever emitted for.
        await WriteFileAsync("export-equals.ts", """
            function helper(): void { }

            export = () => {
                helper();
            };
            """);

        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        Assert.DoesNotContain(nodes, node =>
            node.Id == "typescript:default:export-equals.ts#default" && node.Type == "Function");
        Assert.DoesNotContain(edges, edge =>
            edge.Type == "CALLS" && edge.SourceId == "typescript:default:export-equals.ts#default");
    }

    [Fact]
    public async Task AnonymousDefaultExportedClass_GetsARealNodeForItsMethods()
    {
        // Before the fix, an anonymous default-exported class was never emitted as a
        // node (guarded on tsNode.name), so its HAS_METHOD edge's source id was
        // computed from an empty qualified name — dangling, pointing at a node that
        // was never emitted either.
        await WriteFileAsync("default-class.ts", """
            export default class {
                method(): void { }
            }
            """);

        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        Assert.Contains(nodes, node =>
            node.Id == "typescript:default:default-class.ts#default" && node.Type == "Class");
        Assert.Contains(edges, edge =>
            edge.Type == "HAS_METHOD"
            && edge.SourceId == "typescript:default:default-class.ts#default"
            && edge.TargetId == "typescript:default:default-class.ts#default.method()");
    }

    [Fact]
    public async Task OverloadedCall_ResolvesToTheOverloadTheArgumentsActuallyMatch()
    {
        await WriteFileAsync("overloads.ts", """
            function convert(value: string): string;
            function convert(value: number): string;
            function convert(value: string | number): string {
                return String(value);
            }

            function useConvert(): string {
                return convert(5);
            }
            """);

        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        Assert.Contains(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:overloads.ts#useConvert()"
            && edge.TargetId == "typescript:default:overloads.ts#convert(number)");
        Assert.DoesNotContain(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:overloads.ts#useConvert()"
            && edge.TargetId == "typescript:default:overloads.ts#convert(string)");
    }

    [Fact]
    public async Task CallInsideAStaticBlock_AttributesToTheClassNotFileScope()
    {
        await WriteFileAsync("staticblock.ts", """
            function init(): void { }

            class Config {
                static {
                    init();
                }
            }
            """);

        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        Assert.Contains(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:staticblock.ts#Config"
            && edge.TargetId == "typescript:default:staticblock.ts#init()");
        Assert.DoesNotContain(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:staticblock.ts"
            && edge.TargetId == "typescript:default:staticblock.ts#init()");
    }

    [Fact]
    public async Task NewExpression_WithAnExplicitConstructor_TargetsTheConstructorOverload()
    {
        await WriteFileAsync("construct.ts", """
            class Service {
                constructor(name: string) { }
            }

            function build(): Service {
                return new Service('x');
            }
            """);

        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        Assert.Contains(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:construct.ts#build()"
            && edge.TargetId == "typescript:default:construct.ts#Service.constructor(string)");
    }

    [Fact]
    public async Task NewExpression_WithoutAnExplicitConstructor_FallsBackToTheClassItself()
    {
        await WriteFileAsync("construct2.ts", """
            class Widget { }

            function build(): Widget {
                return new Widget();
            }
            """);

        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        Assert.Contains(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:construct2.ts#build()"
            && edge.TargetId == "typescript:default:construct2.ts#Widget");
    }

    [Fact]
    public async Task OneFileEdit_ReusesTheWorkerProcessAndUpdatesFacts()
    {
        var basePath = await WriteFileAsync("base.ts", "export class Base { }\n");
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var pidBeforeEdit = _pipeline.SessionRegistry.GetSnapshots()
            .Single(s => s.ParserId == "typescript").ProcessId;
        Assert.True(pidBeforeEdit is > 0);

        await File.WriteAllTextAsync(basePath, "export class Base { extra(): number { return 1; } }\n");
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(
            basePath, FileChangeType.Changed, CancellationToken.None);

        var nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        Assert.Contains(nodes, n => n.Name == "extra" && n.Language == "typescript");

        var pidAfterEdit = _pipeline.SessionRegistry.GetSnapshots()
            .Single(s => s.ParserId == "typescript").ProcessId;
        Assert.Equal(pidBeforeEdit, pidAfterEdit); // no process-per-file respawn

        // Deleting the file replaces its facts with nothing.
        File.Delete(basePath);
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(
            basePath, FileChangeType.Deleted, CancellationToken.None);
        nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        Assert.DoesNotContain(nodes, n => n.Language == "typescript");
    }

    [Fact]
    public async Task OneFileEdit_RecomputesSemanticEdgesInUntouchedDependents()
    {
        var basePath = await WriteFileAsync(
            "base.ts", "export class Base { greet(): string { return 'hi'; } }\n");
        await WriteFileAsync(
            "derived.ts",
            "import { Base } from './base';\n" +
            "export class Derived extends Base { run(): string { return this.greet(); } }\n");
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        await File.WriteAllTextAsync(
            basePath, "export class Base { salute(): string { return 'hi'; } }\n");
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(
            basePath, FileChangeType.Changed, CancellationToken.None);

        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        Assert.DoesNotContain(edges, e =>
            e.Type == "CALLS"
            && e.TargetId == "typescript:default:base.ts#Base.greet()");
    }

    [Fact]
    public async Task NativeSyntaxTree_IsAvailableForAnIndexedTypeScriptFile()
    {
        var file = await WriteFileAsync(
            "native.ts", "export const value = (input: number) => input + 1;\n");
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var result = await _pipeline.WorkerService.GetNativeSyntaxTreeAsync(file, maxDepth: 2);

        Assert.Equal("typescript", result.ParserId);
        Assert.Equal("typescript-compiler-syntax-v1", result.Format);
        Assert.Equal("SourceFile", result.Tree.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task NativeSyntaxTree_AtTheMaximumDepth_CrossesTheWorkerBoundary()
    {
        // Same shape as the C# fixture: a 32-level tree is 64+ JSON levels plus the
        // JSON-RPC envelope, past System.Text.Json's default reader limit.
        var nested = string.Concat(Enumerable.Repeat("if (flag) { ", 20))
            + "flag = false;" + string.Concat(Enumerable.Repeat(" }", 20));
        var file = await WriteFileAsync(
            "deep.ts", $"export function deep(flag: boolean): void {{ {nested} }}\n");
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var result = await _pipeline.WorkerService.GetNativeSyntaxTreeAsync(file, maxDepth: 32);

        Assert.True(result.Truncated, "a 32-level cut of a deeper tree reports truncation");
        // 31 levels below the root is already 63 JSON levels before the envelope.
        Assert.True(TreeDepth(result.Tree) >= 31, "the tree arrived at the requested depth");
    }

    private static int TreeDepth(JsonElement node) =>
        node.TryGetProperty("children", out var children)
        && children.ValueKind == JsonValueKind.Array && children.GetArrayLength() > 0
            ? 1 + children.EnumerateArray().Max(TreeDepth)
            : 0;

    // -----------------------------------------------------------------------
    // Incremental-delta emission (mirrors CSharpWorkerIncrementalDeltaTests):
    // whole-tree convergence vs a fresh scan, cross-file dirty propagation, the
    // emitted delta shape, within-file declaration-merging dedup, and no-op
    // suppression — all through the real node worker process.
    // -----------------------------------------------------------------------

    /// <summary>Builds a pipeline whose delta sink is recorded, so a test can assert the
    /// exact shape of what the TypeScript worker emitted while the graph still commits.</summary>
    private CSharpWorkerPipeline RecordingPipeline(out RecordingDeltaSink recorder)
    {
        RecordingDeltaSink? captured = null;
        var pipeline = new CSharpWorkerPipeline(
            _tempDir,
            inner => captured = new RecordingDeltaSink(inner),
            [TypeScriptWorkerRegistration()]);
        recorder = captured!;
        return pipeline;
    }

    [Fact]
    public async Task Incremental_CrossFileEditAddDelete_ConvergesToTheSameGraphAsAFreshScan()
    {
        var basePath = await WriteFileAsync("base.ts",
            "export class Base { greet(): string { return 'hi'; } }\n");
        var derived = await WriteFileAsync("derived.ts",
            "import { Base } from './base';\n" +
            "export class Derived extends Base { run(): string { return this.greet(); } }\n");
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        // The whole incremental repertoire: a cross-file edit (Base.greet -> Base.salute,
        // which rebinds Derived's untouched CALLS/heritage facts), an addition, a deletion.
        await File.WriteAllTextAsync(basePath,
            "export class Base { salute(): string { return 'hi'; } }\n");
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(
            basePath, FileChangeType.Changed, CancellationToken.None);

        var newcomer = await WriteFileAsync("newcomer.ts",
            "export function ping(): number { return 42; }\n");
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(
            newcomer, FileChangeType.Created, CancellationToken.None);

        File.Delete(derived);
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(
            derived, FileChangeType.Deleted, CancellationToken.None);

        var incrementalNodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        var incrementalEdges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();

        // A fresh full scan of the final on-disk tree must produce the identical graph —
        // compared on full records so a FilePath/span divergence cannot hide behind equal ids.
        await using var fresh = new CSharpWorkerPipeline(_tempDir, TypeScriptWorkerRegistration());
        await fresh.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);
        var freshNodes = await fresh.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        var freshEdges = await fresh.RepositoryFactory.CreateEdgeRepository().GetAllAsync();

        Assert.Equal(
            CSharpWorkerIncrementalDeltaTests.NodeRecords(freshNodes),
            CSharpWorkerIncrementalDeltaTests.NodeRecords(incrementalNodes));
        Assert.Equal(
            CSharpWorkerIncrementalDeltaTests.EdgeRecords(freshEdges),
            CSharpWorkerIncrementalDeltaTests.EdgeRecords(incrementalEdges));
    }

    [Fact]
    public async Task Incremental_CrossFileRebind_EmitsFileScopedReplacementIncludingUntouchedDependent()
    {
        await using var pipeline = RecordingPipeline(out var recorder);

        var basePath = await WriteFileAsync("base.ts",
            "export class Base { greet(): string { return 'hi'; } }\n");
        var derived = await WriteFileAsync("derived.ts",
            "import { Base } from './base';\n" +
            "export class Derived extends Base { run(): string { return this.greet(); } }\n");
        await pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);
        recorder.Clear();

        // Editing ONLY base.ts renames greet; Derived's call no longer resolves to
        // base.ts#Base.greet(), so derived's bucket hash flips and it joins the dirty set
        // even though its own bytes never changed — the load-bearing cross-file property.
        await File.WriteAllTextAsync(basePath,
            "export class Base { salute(): string { return 'hi'; } }\n");
        await pipeline.GraphUpdateService.ProcessFileChangeAsync(
            basePath, FileChangeType.Changed, CancellationToken.None);

        var applyDeltas = recorder.Deltas;
        Assert.NotEmpty(applyDeltas);
        // The emission was incremental (never a whole-workspace replacement)...
        Assert.All(applyDeltas, d => Assert.False(d.ReplacesWorkspace));
        // ...and its scope pulled in the cross-file dependent alongside the edited file.
        var replaced = applyDeltas.SelectMany(d => d.ReplacesFiles).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(basePath, replaced);
        Assert.Contains(derived, replaced);

        // The stale cross-file edge is gone from the committed graph.
        var edges = await pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        Assert.DoesNotContain(edges, e =>
            e.Type == "CALLS" && e.TargetId == "typescript:default:base.ts#Base.greet()");
    }

    [Fact]
    public async Task Incremental_DeltaShape_IndexReplacesWorkspace_ApplyScopesToTheDirtyFileVerbatim()
    {
        await using var pipeline = RecordingPipeline(out var recorder);

        var alpha = await WriteFileAsync("alpha.ts", "export class Alpha { m(): void { } }\n");
        await WriteFileAsync("beta.ts", "export class Beta { n(): void { } }\n");
        await pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        // Index deltas replace the whole workspace and never scope to files.
        var indexDeltas = recorder.Deltas;
        Assert.NotEmpty(indexDeltas);
        Assert.All(indexDeltas, d =>
        {
            Assert.True(d.ReplacesWorkspace);
            Assert.Empty(d.ReplacesFiles);
        });

        recorder.Clear();
        // alpha and beta are independent, so editing alpha dirties exactly alpha.
        await File.WriteAllTextAsync(alpha, "export class Alpha { renamed(): void { } }\n");
        await pipeline.GraphUpdateService.ProcessFileChangeAsync(
            alpha, FileChangeType.Changed, CancellationToken.None);

        var applyDeltas = recorder.Deltas;
        Assert.NotEmpty(applyDeltas);
        Assert.All(applyDeltas, d => Assert.False(d.ReplacesWorkspace));
        var replaced = applyDeltas.SelectMany(d => d.ReplacesFiles).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Single(replaced);
        Assert.Contains(alpha, replaced);

        // Emitted nodes belong only to dirty files, and ReplacesFiles carries the VERBATIM
        // path string the worker stamped on those nodes' filePath.
        var emittedNodes = applyDeltas.SelectMany(d => d.Nodes).ToList();
        Assert.NotEmpty(emittedNodes);
        Assert.All(emittedNodes, node => Assert.Contains(node.FilePath!, replaced));
        Assert.Contains(emittedNodes, node => node.Name == "renamed");
    }

    [Fact]
    public async Task DeclarationMerging_WithinFile_KeepsOneNodePerId_LastWins_AndEqualsFreshScan()
    {
        // Cross-file duplicate ids are IMPOSSIBLE in this worker: every node id and edge
        // source id embeds the WALKED file's relative path (typescript:<ws>:<fileRel>#...),
        // so two files can never mint the same id. The dedup that actually bites is
        // within-file declaration merging: two `interface Shared` in ONE file emit the same
        // node id twice from one walk, and last-occurrence-wins must collapse them to one.
        var merge = await WriteFileAsync("merge.ts",
            "export interface Shared { a(): void; }\n" +
            "export interface Shared { b(): void; }\n");
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var nodeRepo = _pipeline.RepositoryFactory.CreateNodeRepository();
        var nodes = await nodeRepo.GetAllAsync();
        const string sharedId = "typescript:default:merge.ts#Shared";
        var shared = nodes.Where(n => n.Id == sharedId).ToList();
        Assert.Single(shared);
        // Last occurrence wins: the surviving type node is the SECOND declaration. The
        // worker emits it 1-based on source line 2; the host normalizes lineBase:1 to
        // 0-based, so the committed StartLine is 1 (the first declaration would be 0).
        Assert.Equal(1, shared[0].StartLine);
        // Both merged members survive under their own distinct ids.
        Assert.Contains(nodes, n => n.Id == "typescript:default:merge.ts#Shared.a()");
        Assert.Contains(nodes, n => n.Id == "typescript:default:merge.ts#Shared.b()");

        // Editing the file keeps exactly one Shared node (dedup is applied every walk).
        await File.WriteAllTextAsync(merge,
            "export interface Shared { a(): void; }\n" +
            "export interface Shared { c(): void; }\n");
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(
            merge, FileChangeType.Changed, CancellationToken.None);
        nodes = await nodeRepo.GetAllAsync();
        Assert.Single(nodes, n => n.Id == sharedId);

        // The incremental end state equals a fresh scan of the same tree (full records).
        var incrementalNodes = await nodeRepo.GetAllAsync();
        var incrementalEdges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        await using var fresh = new CSharpWorkerPipeline(_tempDir, TypeScriptWorkerRegistration());
        await fresh.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);
        Assert.Equal(
            CSharpWorkerIncrementalDeltaTests.NodeRecords(await fresh.RepositoryFactory.CreateNodeRepository().GetAllAsync()),
            CSharpWorkerIncrementalDeltaTests.NodeRecords(incrementalNodes));
        Assert.Equal(
            CSharpWorkerIncrementalDeltaTests.EdgeRecords(await fresh.RepositoryFactory.CreateEdgeRepository().GetAllAsync()),
            CSharpWorkerIncrementalDeltaTests.EdgeRecords(incrementalEdges));
    }

    [Fact]
    public async Task Incremental_NoOpEdit_EmitsSingleEmptyTerminalDelta_AndLeavesGraphUnchanged()
    {
        await using var pipeline = RecordingPipeline(out var recorder);

        // A comment on its own line, replaced later with a SAME-LENGTH comment: file bytes
        // change (so the host forwards the edit) but no emitted fact — not a span, not the
        // module node's end line — moves, so every bucket hashes the same.
        var file = await WriteFileAsync("noop.ts",
            "// aaa\nexport class A { m(): void { } }\n");
        await pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var before = CSharpWorkerIncrementalDeltaTests.NodeRecords(
            await pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync());
        recorder.Clear();

        await File.WriteAllTextAsync(file, "// bbb\nexport class A { m(): void { } }\n");
        await pipeline.GraphUpdateService.ProcessFileChangeAsync(
            file, FileChangeType.Changed, CancellationToken.None);

        // The worker still emits exactly one terminal delta (the supervisor requires ≥1 per
        // mutation), but it carries no facts and no replacesFiles.
        var applyDeltas = recorder.Deltas;
        Assert.NotEmpty(applyDeltas);
        Assert.All(applyDeltas, d =>
        {
            Assert.False(d.ReplacesWorkspace);
            Assert.Empty(d.ReplacesFiles);
            Assert.Empty(d.Nodes);
            Assert.Empty(d.Edges);
        });

        var after = CSharpWorkerIncrementalDeltaTests.NodeRecords(
            await pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync());
        Assert.Equal(before, after);
    }
}
