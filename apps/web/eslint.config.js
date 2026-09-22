// @ts-check
const eslint = require("@eslint/js");
const tseslint = require("typescript-eslint");
const angular = require("angular-eslint");
const nx = require("@nx/eslint-plugin");

/** Everywhere but the price-chart library: the charting engine stays behind the lazy chart route. */
const noChartEngine = ["type:app", "type:feature", "type:data-access", "type:util", "scope:shared"].map(
  (sourceTag) => ({ sourceTag, bannedExternalImports: ["lightweight-charts"] }),
);

module.exports = tseslint.config(
  {
    // Never lint build output or caches — they contain vendored, minified code with
    // inline eslint-disable directives for rules this config does not load.
    ignores: ["dist/**", ".angular/**", ".nx/**", "coverage/**", "node_modules/**"],
  },
  {
    files: ["**/*.ts"],
    extends: [
      eslint.configs.recommended,
      ...tseslint.configs.recommended,
      ...tseslint.configs.stylistic,
      ...angular.configs.tsRecommended,
    ],
    processor: angular.processInlineTemplates,
    plugins: { "@nx": nx },
    rules: {
      // The library graph, enforced. Two axes, and an import must satisfy both:
      //
      // TYPE — app > feature > ui > data-access > util. A feature never imports another
      // feature: each is a lazy route and must load on its own. `ui` may read state
      // (a theme, a manifest) but a store never reaches up into a component.
      //
      // SCOPE — a domain sees itself and `shared`. `market` also reads `portfolio` and
      // `orders`, because the chart marks the user's positions and working orders; the
      // TYPE axis limits that to their data-access libraries.
      //
      // The same rule also rejects deep imports into a library's `src/lib` and a static
      // import of a library that a route loads lazily.
      "@nx/enforce-module-boundaries": [
        "error",
        {
          allow: [],
          depConstraints: [
            { sourceTag: "type:app", onlyDependOnLibsWithTags: ["type:feature", "type:ui", "type:data-access", "type:util"] },
            { sourceTag: "type:feature", onlyDependOnLibsWithTags: ["type:ui", "type:data-access", "type:util"] },
            { sourceTag: "type:ui", onlyDependOnLibsWithTags: ["type:ui", "type:data-access", "type:util"] },
            { sourceTag: "type:data-access", onlyDependOnLibsWithTags: ["type:data-access", "type:util"] },
            { sourceTag: "type:util", onlyDependOnLibsWithTags: ["type:util"] },
            { sourceTag: "scope:shared", onlyDependOnLibsWithTags: ["scope:shared"] },
            { sourceTag: "scope:portfolio", onlyDependOnLibsWithTags: ["scope:portfolio", "scope:shared"] },
            { sourceTag: "scope:orders", onlyDependOnLibsWithTags: ["scope:orders", "scope:shared"] },
            { sourceTag: "scope:account", onlyDependOnLibsWithTags: ["scope:account", "scope:shared"] },
            { sourceTag: "scope:market", onlyDependOnLibsWithTags: ["scope:market", "scope:shared", "scope:portfolio", "scope:orders"] },
            ...noChartEngine,
          ],
        },
      ],
      // No `any`: every wire type is modelled explicitly in @akshaya/shared/models so the compiler
      // catches a manifest/DTO drift instead of it surfacing as a runtime `undefined`.
      "@typescript-eslint/no-explicit-any": "error",
      "@typescript-eslint/explicit-function-return-type": "off",
      "@typescript-eslint/no-unused-vars": ["error", { argsIgnorePattern: "^_" }],
      "@angular-eslint/prefer-standalone": "error",
      "@angular-eslint/component-class-suffix": "error",
      "@angular-eslint/directive-class-suffix": "error",
      "@angular-eslint/no-input-rename": "off",
      "@angular-eslint/component-selector": [
        "error",
        { type: "element", prefix: "ak", style: "kebab-case" },
      ],
      "@angular-eslint/directive-selector": [
        "error",
        { type: "attribute", prefix: "ak", style: "camelCase" },
      ],
    },
  },
  {
    files: ["**/*.html"],
    extends: [
      ...angular.configs.templateRecommended,
      ...angular.configs.templateAccessibility,
    ],
    rules: {},
  },
);
