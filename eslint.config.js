// Check-only lint gate for the hand-authored web bundle (src/App/web) - #301
// item 8. Correctness rules only: the bundle is deliberately hand-written and
// vendored (ADR-001), so no formatter and no auto-fix ever runs against it
// (CI separately verifies the published bundle byte-identical to source).
// Lives at the repo root because ESLint flat config cannot match files above
// its own directory; the eslint binary itself is installed in
// tests/graph-bundle/node_modules (the repo's single Node footprint).
// Run via: pwsh tools/lint-web.ps1
export default [
  {
    files: ['src/App/web/*.js'],
    ignores: ['src/App/web/vendor/**'],
    languageOptions: {
      ecmaVersion: 2022,
      sourceType: 'script',
      // Dependency-free on purpose (the repo root has no node_modules): the
      // handful of browser globals the bundle uses, listed by hand, plus the
      // vendored cytoscape entry point. An unlisted global = no-undef = the
      // gate working as intended - extend this list deliberately.
      globals: {
        window: 'readonly',
        document: 'readonly',
        console: 'readonly',
        navigator: 'readonly',
        fetch: 'readonly',
        setInterval: 'readonly',
        clearInterval: 'readonly',
        setTimeout: 'readonly',
        clearTimeout: 'readonly',
        requestAnimationFrame: 'readonly',
        cytoscape: 'readonly',
      },
    },
    linterOptions: {
      reportUnusedDisableDirectives: 'error',
    },
    rules: {
      // The eslint:recommended correctness core, enumerated explicitly (no
      // plugin package) so the gate's exact scope is auditable in one place.
      'no-undef': 'error',
      // caughtErrors none: `catch (err) {}` with the binding unused is the
      // bundle's deliberate never-throw idiom (bridge errors must not take the
      // page down), not dead code.
      'no-unused-vars': ['error', { args: 'none', caughtErrors: 'none' }],
      'no-dupe-keys': 'error',
      'no-dupe-args': 'error',
      'no-duplicate-case': 'error',
      'no-unreachable': 'error',
      'no-fallthrough': 'error',
      'no-redeclare': 'error',
      'no-self-assign': 'error',
      'no-unsafe-negation': 'error',
      'no-cond-assign': 'error',
      'no-constant-condition': ['error', { checkLoops: false }],
      'no-empty': ['error', { allowEmptyCatch: true }],
      'no-func-assign': 'error',
      'no-global-assign': 'error',
      'no-sparse-arrays': 'error',
      'use-isnan': 'error',
      'valid-typeof': 'error',
      eqeqeq: ['error', 'smart'],
    },
  },
];
