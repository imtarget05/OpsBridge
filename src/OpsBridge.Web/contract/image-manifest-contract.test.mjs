/**
 * THE IMAGE/MANIFEST VOLUME CONTRACT.
 *
 * The web tier is two artefacts that were written by two different slices and
 * merged: the image (`Dockerfile.web`) and the Kubernetes manifests
 * (`infrastructure/kubernetes/base/opsbridge-web/`). Nothing but a reader's
 * attention connected them, and the first version of the manifest was WRONG in
 * a way only a running pod could reveal:
 *
 *   * the image declares exactly three writable paths — `/tmp/nginx/conf.d`,
 *     `/tmp/nginx/cache` and `/tmp/nginx/run` — and creates them at build time
 *     with `chown opsbridge:opsbridge`;
 *   * the manifest mounted `emptyDir`s at `/var/cache/nginx`, `/var/run` and
 *     `/tmp` instead.
 *
 * The first two are paths this image never writes to. The third is worse than
 * useless: an `emptyDir` mounted at `/tmp` REPLACES the image's whole `/tmp`
 * tree, so the pre-created, pre-owned `conf.d`, `cache` and `run` directories
 * disappear and the container is left with a volume it would have to create
 * them in itself — the very thing `readOnlyRootFilesystem: true` forbids. The
 * image-only rehearsal never saw it, because that rehearsal ran the container
 * with the image's OWN tmpfs layout.
 *
 * This test is the control that stops the two halves from drifting apart
 * again. It reads BOTH sides and fails in BOTH directions:
 *
 *   * a `mountPath` the image does not declare (a stray mount, or `/tmp`
 *     mounted wholesale) is a failure;
 *   * a writable path the image declares that the manifest does not mount is a
 *     failure.
 *
 * EVERY container is checked, not just the first one. A manifest that adds a
 * sidecar which mounts `/tmp` wholesale breaks the contract just as much as a
 * broken main container does — the sidecar runs in the same pod and gets the
 * same volumes — so a check that only looked at the FIRST `volumeMounts:`
 * block would report "4 pass" for a manifest that cannot start.
 *
 * Run it with `npm run test:contract` (from `src/OpsBridge.Web`).
 *
 * The two paths can be overridden, which is what makes the negative controls
 * possible without touching the repository. The ENVIRONMENT VARIABLES are the
 * reliable way to do it (`node --test` does not forward command-line
 * arguments to the test file it runs); the command-line flags are the
 * convenience for a direct `node contract/image-manifest-contract.test.mjs`.
 *
 *   WEB_CONTRACT_DOCKERFILE=/tmp/mutated/Dockerfile.web \
 *   WEB_CONTRACT_DEPLOYMENT=/tmp/mutated/deployment.yaml \
 *     node --test contract/
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(HERE, '..', '..', '..');

/** Reads an `--flag=value` / `--flag value` argument from the command line. */
function argValue(name) {
  const argv = process.argv.slice(2);
  const inline = argv.find((a) => a.startsWith(`--${name}=`));
  if (inline) {
    return inline.slice(`--${name}=`.length);
  }
  const index = argv.indexOf(`--${name}`);
  if (index >= 0 && index + 1 < argv.length) {
    return argv[index + 1];
  }
  return undefined;
}

const DOCKERFILE_PATH = process.env.WEB_CONTRACT_DOCKERFILE
  ?? argValue('dockerfile')
  ?? path.join(REPO_ROOT, 'Dockerfile.web');
const DEPLOYMENT_PATH = process.env.WEB_CONTRACT_DEPLOYMENT
  ?? argValue('deployment')
  ?? path.join(REPO_ROOT, 'infrastructure', 'kubernetes', 'base', 'opsbridge-web', 'deployment.yaml');

/**
 * The writable paths the IMAGE declares.
 *
 * They are not read from the documentation comment: they are read from the
 * `RUN` instruction that creates them and chowns them to the runtime user,
 * because that is the filesystem fact. The documentation comment is checked
 * against this set separately (see "the image documents the paths it creates").
 *
 * THE SET IS DERIVED FROM TWO INDEPENDENT FACTS OF THE IMAGE, not from one:
 *
 *   * the `mkdir -p` list - the directories the image CREATES;
 *   * the `chown` target - the subtree the image HANDS to the runtime user.
 *
 * The set is the created directories that are NOT content roots (the
 * directories the image FILLS with a `COPY`: those hold the static bundle and
 * are read-only at run time), and every one of them has to be covered by the
 * `chown` target. Deriving it from the `mkdir` list is what keeps a NARROWED
 * `chown` from silently shrinking the contract: a `chown` of
 * `/tmp/nginx/conf.d` alone (together with a doc comment that lists only that
 * one path) would have produced a one-element writable set, the manifest
 * would then have had to stop mounting `cache` and `run` — and the contract
 * test would have been GREEN on a manifest that breaks the pod. The assertion
 * below makes that impossible: the created directories that the `chown` does
 * not cover are a hard failure, whatever the documentation says.
 */
export function imageWritablePaths(dockerfileText) {
  // Join the continued lines of the RUN instructions, so the instruction is
  // one logical line.
  const instructions = dockerfileText
    .split('\n')
    .reduce((acc, line) => {
      const trimmed = line.trim();
      if (acc.length > 0 && acc[acc.length - 1].endsWith('\\')) {
        acc[acc.length - 1] = `${acc[acc.length - 1].slice(0, -1)} ${trimmed}`;
      } else {
        acc.push(trimmed);
      }
      return acc;
    }, []);

  const creating = instructions.find(
    (i) => i.startsWith('RUN ') && i.includes('mkdir -p') && i.includes('chown -R'),
  );
  assert.ok(creating, 'the Dockerfile has no RUN instruction that creates and chowns the writable paths');

  // The chown target is the subtree the runtime user owns.
  const chown = creating.match(/chown -R\s+\S+\s+(\S+)/);
  assert.ok(chown, 'the Dockerfile does not chown the writable paths to the runtime user');
  const writableRoot = chown[1].replace(/\/+$/, '');

  const mkdir = creating.match(/mkdir -p\s+([^\s&|]+(?:\s+[^\s&|]+)*?)(?:\s+&|&&|$)/);
  assert.ok(mkdir, 'the Dockerfile does not create the writable paths');
  const created = mkdir[1]
    .split(/\s+/)
    .filter((p) => p.startsWith('/'))
    .map((p) => p.replace(/\/+$/, ''));

  // The content roots: the directories the image FILLS with a COPY. They hold
  // the static bundle, so they are read-only at run time and must NOT be
  // mounted as volumes (mounting one would hide the bundle).
  const contentRoots = instructions
    .filter((i) => i.startsWith('COPY '))
    .map((i) => {
      const parts = i.split(/\s+/);
      const destination = parts[parts.length - 1];
      return destination.replace(/\/+$/, '');
    })
    .filter((d) => d.startsWith('/'));

  const isUnder = (p, root) => p === root || p.startsWith(`${root}/`);
  const writable = created.filter((p) => !contentRoots.some((root) => isUnder(p, root)));
  assert.ok(
    writable.length > 0,
    `the Dockerfile creates nothing but content (its writable paths are all COPY destinations)`,
  );

  // The chown has to cover EVERY writable path: the runtime user has to own
  // the directories it writes to. A narrowed chown is a hard failure here
  // rather than a silently smaller contract.
  const uncovered = writable.filter((p) => !isUnder(p, writableRoot));
  assert.ok(
    uncovered.length === 0,
    `the Dockerfile creates ${uncovered.join(', ')} but chowns only ${writableRoot} to the runtime user; ` +
      `a writable path the runtime user does not own is a path it cannot write to`,
  );

  return { writable, writableRoot, contentRoots };
}

/** The writable paths the image's contract comment DOCUMENTS. */
export function documentedWritablePaths(dockerfileText) {
  const lines = dockerfileText.split('\n');
  const start = lines.findIndex((l) => /^\s*#\s*writable paths\s+\S/.test(l));
  assert.ok(start >= 0, "the Dockerfile's contract comment does not state the writable paths");

  const paths = [];
  const first = lines[start].match(/^\s*#\s*writable paths\s+(\S+)/);
  if (first) {
    paths.push(first[1]);
  }
  for (let i = start + 1; i < lines.length; i += 1) {
    const line = lines[i];
    if (!/^\s*#/.test(line)) {
      break;
    }
    const continuation = line.match(/^\s*#\s+(\/[^\s]+)/);
    if (continuation) {
      paths.push(continuation[1]);
    } else {
      break;
    }
  }
  return paths;
}

/**
 * The `mountPath`s of EVERY container of the Deployment, with the volume name
 * each belongs to and the container it belongs to.
 *
 * LIMITS OF THIS SCANNER (deliberate, and stated here because a reader has to
 * be able to trust it):
 *
 *   * it is a LINE scanner, not a YAML parser: Node has no YAML parser in the
 *     standard library and this repository's web package deliberately has no
 *     YAML dependency, so a real parse would mean a new dependency;
 *   * it reads the BLOCK form of `volumeMounts:` (a list of `- name:` /
 *     `mountPath:` mappings under it) in both key orders, which is what both
 *     the hand-written manifest and the `kustomize` render use;
 *   * it does not read the inline flow form (`volumeMounts: [{name: x,
 *     mountPath: /y}]`). Neither the manifest nor its render uses it, and a
 *     manifest that switched to it would lose its mounts here — which makes
 *     the "every writable path is mounted" direction FAIL, i.e. the test
 *     fails loudly instead of passing vacuously.
 *
 * It scans the WHOLE document: every `containers:` list, every container in
 * it, every `volumeMounts:` block of every container. A sidecar that mounts
 * `/tmp` wholesale is found exactly like the main container's mounts are.
 */
export function deploymentMounts(deploymentText) {
  const lines = deploymentText.split('\n');
  const mounts = [];

  // Every `containers:` list in the document.
  for (let i = 0; i < lines.length; i += 1) {
    const containerList = lines[i].match(/^(\s*)containers:\s*$/);
    if (!containerList) {
      continue;
    }
    const listIndent = containerList[1].length;

    let j = i + 1;
    while (j < lines.length) {
      const line = lines[j];
      if (line.trim() === '') {
        j += 1;
        continue;
      }
      const item = line.match(/^(\s*)-\s*(.*)$/);
      if (!item || item[1].length <= listIndent) {
        break; // the list of containers ended
      }
      const itemIndent = item[1].length;

      // The container's own lines: the item line plus every more indented one.
      const block = [{ indent: itemIndent, text: item[2] }];
      let k = j + 1;
      while (k < lines.length) {
        const next = lines[k];
        if (next.trim() === '') {
          k += 1;
          continue;
        }
        const indent = next.match(/^\s*/)[0].length;
        if (indent <= itemIndent) {
          break;
        }
        block.push({ indent, text: next.trim() });
        k += 1;
      }

      const containerName = containerNameOf(block, itemIndent);
      for (const mount of volumeMountsOf(block)) {
        mounts.push({ ...mount, container: containerName });
      }

      j = k;
    }
  }

  return mounts.filter((m) => m.mountPath !== null);
}

/** The name of a container: on the `- name: x` item line, or on its own line. */
function containerNameOf(block, itemIndent) {
  const onItemLine = block[0].text.match(/^name:\s*(\S+)/);
  if (onItemLine) {
    return onItemLine[1];
  }
  const own = block.find((l) => l.indent === itemIndent + 2 && /^name:\s*\S+/.test(l.text));
  return own ? own.text.match(/^name:\s*(\S+)/)[1] : '(unnamed container)';
}

/** The `volumeMounts:` entries of one container block. */
function volumeMountsOf(block) {
  const mounts = [];
  let i = 0;
  while (i < block.length) {
    const header = block[i].text.match(/^volumeMounts:\s*$/);
    if (!header) {
      i += 1;
      continue;
    }
    const headerIndent = block[i].indent;
    i += 1;

    let current = null;
    while (i < block.length) {
      const line = block[i];
      if (line.indent <= headerIndent) {
        break; // the volumeMounts block ended
      }
      const item = line.text.match(/^-\s*(.*)$/);
      if (item) {
        current = { name: null, mountPath: null };
        mounts.push(current);
        const name = item[1].match(/^name:\s*(\S+)/);
        if (name) {
          current.name = name[1];
        }
        const mountPath = item[1].match(/mountPath:\s*(\S+)/);
        if (mountPath) {
          current.mountPath = mountPath[1];
        }
        i += 1;
        continue;
      }
      const mountPath = line.text.match(/^mountPath:\s*(\S+)/);
      if (mountPath && current) {
        current.mountPath = mountPath[1];
        i += 1;
        continue;
      }
      const name = line.text.match(/^name:\s*(\S+)/);
      if (name && current) {
        current.name = name[1];
        i += 1;
        continue;
      }
      i += 1;
    }
  }
  return mounts;
}

/**
 * THE CONTRACT, as a function, so that the negative controls can call it with
 * a broken manifest instead of only ever seeing the happy path.
 */
export function assertVolumeContract(dockerfileText, deploymentText) {
  const { writable } = imageWritablePaths(dockerfileText);
  const mounts = deploymentMounts(deploymentText);
  const mounted = mounts.map((m) => m.mountPath);

  assert.ok(
    writable.length > 0,
    'the image declares no writable path (the contract would be vacuous)',
  );
  assert.ok(
    mounted.length > 0,
    'the web Deployment mounts no path (the contract would be vacuous)',
  );

  // Every violation is collected, and the contract fails with ALL of them:
  // a report that stops at the first problem hides the others, and the
  // negative controls below have to be able to look for their own violation
  // in a manifest that may carry more than one.
  const problems = [];

  // Direction 1: every mountPath of EVERY container is a writable path the
  // image declares. A stray mount — `/var/cache/nginx`, `/var/run`, or `/tmp`
  // mounted WHOLESALE — is a failure: the first two are paths this image
  // never writes to, and the third replaces the image's whole /tmp tree (and
  // with it the pre-created, pre-owned conf.d/cache/run directories).
  for (const mount of mounts) {
    if (!writable.includes(mount.mountPath)) {
      problems.push(
        `the web Deployment mounts ${mount.mountPath} in container ${mount.container}, ` +
          `which the image does not declare as writable ` +
          `(the image's writable paths are: ${writable.join(', ')})`,
      );
    }
  }

  // Direction 2: every writable path the image declares is mounted. A path
  // the image writes to and the pod does not mount is a path the container
  // cannot write to — with readOnlyRootFilesystem: true, nginx dies.
  for (const writablePath of writable) {
    if (!mounted.includes(writablePath)) {
      problems.push(
        `the image writes to ${writablePath}, but the web Deployment does not mount it ` +
          `(the manifest mounts: ${mounted.join(', ')})`,
      );
    }
  }

  assert.ok(problems.length === 0, `the image and the manifest disagree:\n  - ${problems.join('\n  - ')}`);

  return { writable, mounts };
}

const dockerfile = readFileSync(DOCKERFILE_PATH, 'utf8');
const deployment = readFileSync(DEPLOYMENT_PATH, 'utf8');

test('the image and the manifest agree on the writable paths', () => {
  assertVolumeContract(dockerfile, deployment);
});

test('no mount covers a writable path only by accident', () => {
  const { writable } = imageWritablePaths(dockerfile);
  const mounts = deploymentMounts(deployment);

  for (const mount of mounts) {
    for (const writablePath of writable) {
      assert.ok(
        !(writablePath !== mount.mountPath && writablePath.startsWith(`${mount.mountPath}/`)),
        `the mount ${mount.mountPath} (container ${mount.container}) covers the writable path ` +
          `${writablePath} by swallowing it instead of mounting it: mounting a PARENT of a writable ` +
          `path replaces the image's own directory for it`,
      );
    }
  }
});

test('the image documents the paths it creates', () => {
  const { writable } = imageWritablePaths(dockerfile);
  const documented = documentedWritablePaths(dockerfile);

  assert.deepEqual(
    [...documented].sort(),
    [...writable].sort(),
    "the Dockerfile's contract comment and the directories it creates disagree",
  );
});

test('the pod can actually write to the mounted volumes', () => {
  // fsGroup makes an emptyDir group-writable; runAsGroup puts the process in
  // that group; runAsUser/runAsNonRoot and readOnlyRootFilesystem are the
  // hardening the rest of the tree relies on. Without them the volumes are
  // root-owned and uid 1654 cannot write into them.
  assert.match(deployment, /^\s*fsGroup:\s*1654\s*$/m, 'the pod does not declare fsGroup: 1654');
  assert.match(deployment, /^\s*runAsGroup:\s*1654\s*$/m, 'the pod does not declare runAsGroup: 1654');
  assert.match(deployment, /^\s*runAsUser:\s*1654\s*$/m, 'the pod does not declare runAsUser: 1654');
  assert.match(deployment, /^\s*runAsNonRoot:\s*true\s*$/m, 'the pod does not declare runAsNonRoot: true');
  assert.match(
    deployment,
    /^\s*readOnlyRootFilesystem:\s*true\s*$/m,
    'the container does not declare readOnlyRootFilesystem: true',
  );
});

// ---------------------------------------------------------------------------
// NEGATIVE CONTROLS
//
// A contract test that cannot fail documents nothing. Each control below
// feeds the contract a BROKEN input and asserts that the contract rejects
// it — the same discipline scripts/test-validate-manifests.py applies to the
// manifest validator.
// ---------------------------------------------------------------------------

test('NEGATIVE CONTROL: a sidecar that mounts /tmp wholesale is rejected', () => {
  // The case the first version of this test could not see: the contract was
  // checked against the FIRST `volumeMounts:` block only, so a manifest with
  // a second container that mounts /tmp wholesale still reported "4 pass".
  const sidecar = `
        - name: sidecar
          image: busybox:1.36
          volumeMounts:
            - name: tmp-whole
              mountPath: /tmp
`;
  const broken = deployment.replace(
    /(\n      volumes:\n)/,
    `${sidecar}$1`,
  );
  assert.notEqual(broken, deployment, 'the mutation did not apply - the fixture changed');

  assert.throws(
    () => assertVolumeContract(dockerfile, broken),
    /mounts \/tmp in container sidecar/,
    'a sidecar that mounts /tmp wholesale must fail the contract',
  );
});

test('NEGATIVE CONTROL: a second container with a stray mount is rejected', () => {
  const sidecar = `
        - name: sidecar
          image: busybox:1.36
          volumeMounts:
            - name: var-run
              mountPath: /var/run
`;
  const broken = deployment.replace(/(\n      volumes:\n)/, `${sidecar}$1`);
  assert.notEqual(broken, deployment, 'the mutation did not apply - the fixture changed');

  assert.throws(
    () => assertVolumeContract(dockerfile, broken),
    /mounts \/var\/run in container sidecar/,
    'a sidecar that mounts a path the image does not declare must fail the contract',
  );
});
test('NEGATIVE CONTROL: a narrowed chown cannot silently shrink the writable set', () => {
  // The chown target and the documentation comment are changed together -
  // the coupled change that used to shrink the contract to one path. The
  // writable set is derived from the `mkdir` list as well, so the created
  // directories the narrowed chown does not cover are a hard failure.
  const narrowed = dockerfile
    .replace('chown -R opsbridge:opsbridge /tmp/nginx', 'chown -R opsbridge:opsbridge /tmp/nginx/conf.d')
    .replace(
      /#\s*writable paths\s+\/tmp\/nginx\/conf\.d\n(#\s+\/tmp\/nginx\/(?:cache|run)\n)+/,
      '#   writable paths   /tmp/nginx/conf.d\n',
    );
  assert.notEqual(narrowed, dockerfile, 'the mutation did not apply - the fixture changed');

  assert.throws(
    () => imageWritablePaths(narrowed),
    /chowns only \/tmp\/nginx\/conf\.d/,
    'a narrowed chown must not shrink the writable set silently',
  );
});
