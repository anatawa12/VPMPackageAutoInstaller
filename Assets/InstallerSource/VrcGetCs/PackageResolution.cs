using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using Version = SemanticVersioning.Version;
// ReSharper disable LocalVariableHidesMember

// ReSharper disable InconsistentNaming

namespace Anatawa12.VrcGet
{
    static class package_resolution
    {
        readonly struct PackageQueue
        {
            [NotNull] public readonly LinkedList<PackageInfo> pending_queue;

            public PackageQueue(LinkedList<PackageInfo> pendingQueue) => pending_queue = pendingQueue;

            public PackageInfo? next_package() => pending_queue.pop_back();

            public PackageInfo? find_pending_package(string name)
            {
                //pending_queue.iter().find(|x| x.name() == name)
                foreach (var x in pending_queue.Where(x => x.name() == name))
                    return x;

                return null;
            }

            public void add_pending_package(PackageInfo package)
            {   
                this.pending_queue.retain(x => x.name() != package.name());
                this.pending_queue.push_back(package);
            }
        }

        partial class ResolutionContext
        {
            private bool allow_prerelease;
            public PackageQueue pending_queue;
            [NotNull] private Dictionary<string, DependencyInfo> dependencies;
        }

        /*
        struct Legacy<'env>(&'env Vec<String>);

        impl<'env> Default for Legacy<'env> {
            fn default()->Self {
                static VEC:
                Vec < String > = Vec::new();
                Self(&VEC)
            }
        }*/

        
        class DependencyInfo
        {
            public PackageInfo? @using;
            [CanBeNull] public Version current;
            // "" key for root dependencies
            [NotNull] public readonly Dictionary<string, VersionRange> requirements;
            [NotNull] public HashSet<string> dependencies;

            [NotNull] public HashSet<string> modern_packages;
            [NotNull] public string[] legacy_packages;

            public bool allow_pre;
            public bool touched;

            public DependencyInfo()
            {
                @using = null;
                current = null;
                requirements = new Dictionary<string, VersionRange> {};
                dependencies = new HashSet<string>();
                modern_packages = new HashSet<string>();
                legacy_packages = Array.Empty<string>();
            }

            public DependencyInfo(VersionRange range, bool allow_pre) : this()
            {
                requirements.Add("", range);
                this.allow_pre = allow_pre;
            }

            public void add_range(string source, VersionRange range) {
                requirements[source] = range;
                touched = true;
            }

            public void remove_range(string source) {
                requirements.Remove(source);
                touched = true;
            }

            public void add_modern_package(string modern) {
                modern_packages.Add(modern);
                touched = true;
            }

            public void remove_modern_package(string modern) {
                modern_packages.Remove(modern);
                touched = true;
            }

            public bool is_legacy() {
                return modern_packages.len() != 0;
            }

            public void set_using_info(Version version, HashSet<string> dependencies) {
                this.allow_pre |= version.IsPreRelease;
                current = version;
                this.dependencies = dependencies;
            }
        }

        partial class ResolutionContext
        {
            public ResolutionContext(bool allow_prerelease, IEnumerable<PackageInfo> packages)
            {
                this.dependencies = new Dictionary<string, DependencyInfo>();
                this.pending_queue = new PackageQueue(new LinkedList<PackageInfo>(packages));
                this.allow_prerelease = allow_prerelease;

                foreach (var pkg in this.pending_queue.pending_queue)
                {
                    this.dependencies.entry_or_default(pkg.name()).allow_pre = true;
                }
            }
        }

        partial class ResolutionContext {
            public void add_root_dependency(string name, VersionRange range, bool allow_pre) {
                this.dependencies.insert(name, new DependencyInfo(range, allow_pre));
            }

            public void add_locked_dependency(string name, VpmLockedDependency locked, Environment env) {
                var info = this.dependencies.entry_or_default(name);
                info.set_using_info(locked.version, new HashSet<string>(locked.dependencies.Keys));

                if (env.find_package_by_name(name, PackageSelector.specific_version(locked.version)) is PackageInfo pkg) {
                    info.legacy_packages = pkg.legacy_packages();

                    foreach (var legacy in pkg.legacy_packages()) {
                        this.dependencies.entry_or_default(legacy).modern_packages.insert(name);
                    }
                }

                foreach (var (dependency, range) in locked.dependencies) {
                    this.dependencies.entry_or_default(dependency).requirements.insert(name, range);
                }
            }

            public bool add_package(PackageInfo package) {
                var entry = this.dependencies.entry_or_default(package.name());

                if (entry.is_legacy()) {
                    return false;
                }

                var vpm_dependencies = package.vpm_dependencies();
                var legacy_packages = package.legacy_packages();
                var name = package.name();

                entry.touched = true;
                entry.current = package.version();
                entry.@using = package;

                var old_dependencies = CsUtils.replace(ref entry.dependencies, new HashSet<string>(vpm_dependencies.Keys));
                var old_legacy_packages = CsUtils.replace(ref entry.legacy_packages, legacy_packages);

                // region process dependencies
                // remove previous dependencies if exists
                foreach (var dep in old_dependencies) {
                    this.dependencies[dep].remove_range(name);
                }
                foreach (var (dependency, range) in vpm_dependencies) {
                    this.dependencies.entry_or_default(dependency).add_range(name, range);
                }
                // endregion

                // region process modern packages
                foreach (var dep in old_legacy_packages) {
                    this.dependencies[dep].remove_modern_package(name);
                }
                foreach (var legacy in legacy_packages) {
                    this.dependencies.entry_or_default(legacy).add_modern_package(name);
                }
                // endregion

                return true;
            }

            public bool should_add_package(string name, VersionRange range) {
                var entry = this.dependencies[name];

                if (entry.is_legacy()) {
                    return false;
                }

                var install = true;
                var allow_prerelease = entry.allow_pre || this.allow_prerelease;

                if (this.pending_queue.find_pending_package(name) is PackageInfo pending) {
                    if (range.match_pre(pending.version(), allow_prerelease)) {
                        // if installing version is good, no need to reinstall
                        install = false;
                        //log::debug!("processing package {name}: dependency {name} version {range}: pending matches");
                    }
                } else {
                    // if already installed version is good, no need to reinstall
                    if (entry.current is Version version) {
                        if (range.match_pre(version, allow_prerelease)) {
                            //log::debug!("processing package {name}: dependency {name} version {range}: existing matches");
                            install = false;
                        }
                    }
                }

                return install;
            }
        }

        partial class ResolutionContext {
            public PackageResolutionResult build_result() {
                var conflicts = new Dictionary<string, List<string>>();
                foreach (var (name, info) in this.dependencies) {
                    if (!info.is_legacy() && info.touched) {
                        if (info.current is Version version) {
                            foreach (var (source, range) in info.requirements) {
                                if (!range.match_pre(version, info.allow_pre || this.allow_prerelease)) {
                                    conflicts.entry_or_default(name).push(source);
                                }
                            }
                        }
                    }
                }

                var found_legacy_packages = this.dependencies
                        .Where( p => p.Value.is_legacy())
                    .Select(p => p.Key)
                        .ToArray();

                var new_packages = this.dependencies
                        .Values
                        .Where( info => !info.is_legacy())
                        .Where( info => info.@using != null)
                    .Select( x => x.@using.Value)
                    .ToArray();

                return new PackageResolutionResult(
                    new_packages: new_packages,
                    conflicts: conflicts,
                    found_legacy_packages: found_legacy_packages);
            }
        }

        public readonly struct PackageResolutionResult {
            public readonly PackageInfo[] new_packages;
            // conflict dependency -> conflicting package[])
            public readonly Dictionary<string, List<string>> conflicts;
                // list of names of legacy packages we found
            public readonly string[] found_legacy_packages;

            public PackageResolutionResult(PackageInfo[] new_packages, Dictionary<string, List<string>> conflicts, string[] found_legacy_packages)
            {
                this.new_packages = new_packages;
                this.conflicts = conflicts;
                this.found_legacy_packages = found_legacy_packages;
            }
        }

        public static PackageResolutionResult collect_adding_packages(
            IReadOnlyDictionary<string, VpmDependency> dependencies,
            IReadOnlyDictionary<string, VpmLockedDependency> locked_dependencies,
            [CanBeNull] UnityVersion unity_version,
            Environment env,
            IEnumerable<PackageInfo> packages,
            bool allow_prerelease
        ) {
            var context = new ResolutionContext(allow_prerelease, packages);

            // first, add dependencies
            // VPAI: we don't need root_dependencies because we have heap
            //var root_dependencies = Vec::with_capacity(dependencies.len());

            foreach (var (name, dependency) in dependencies) {
                VersionRange range;
                bool allow_pre;

                if (dependency.version.as_single_version() is Version min_ver) {
                    allow_pre = min_ver.is_pre();
                    if (locked_dependencies.get(name) is VpmLockedDependency locked) {
                        allow_pre |= locked.version.IsPreRelease;
                        if (locked.version < min_ver){
                            min_ver = locked.version;
                        }
                    }
                    range = VersionRange.same_or_later(min_ver);
                } else {
                    range = dependency.version.as_range();
                    allow_pre = range.contains_pre();
                }

                //root_dependencies.push((name, range, allow_pre));
                //}

                //for (name, range, allow_pre) in &root_dependencies
                //{
                context.add_root_dependency(name, range, allow_pre);
            }

            // then, add locked dependencies info
            foreach (var (source, locked) in locked_dependencies) {
                context.add_locked_dependency(source, locked, env);
            }

            while (context.pending_queue.next_package() is PackageInfo x) {
                //log::debug!("processing package {} version {}", x.name(), x.version());
                var name = x.name();
                var vpm_dependencies = x.vpm_dependencies();

                if (context.add_package(x)) {
                    // add new dependencies
                    foreach (var (dependency, range) in vpm_dependencies) {
                        //log::debug!("processing package {name}: dependency {dependency} version {range}");

                        if (context.should_add_package(dependency, range)) {
                            var found = env.find_package_by_name(dependency, PackageSelector.range_for(unity_version, range))
                                        ?? env.find_package_by_name(dependency, PackageSelector.range_for(null, range));
                            if (found == null)
                                throw new VrcGetException($"dependency not found: {dependency}");

                            // remove existing if existing
                            context.pending_queue.add_pending_package(found.Value);
                        }
                    }
                }
            }

            return context.build_result();
        }
    }
}