// @ts-check
const eslint = require('@eslint/js');
const { defineConfig } = require('eslint/config');
const tseslint = require('typescript-eslint');
const angular = require('angular-eslint');

/** ECharts' values (its types are free to import). */
const echarts = {
  regex: '^echarts(/|$)',
  allowTypeImports: true,
  message: 'ECharts loads with the first chart shown: through ECHARTS_IMPORT (core/charts).',
};

module.exports = defineConfig([
  {
    // Made from the API's document (npm run api).
    ignores: ['src/app/core/api/schema.d.ts'],
  },
  {
    files: ['**/*.ts'],
    extends: [
      eslint.configs.recommended,
      tseslint.configs.recommended,
      tseslint.configs.stylistic,
      angular.configs.tsRecommended,
    ],
    processor: angular.processInlineTemplates,
    rules: {
      '@angular-eslint/directive-selector': [
        'error',
        {
          type: 'attribute',
          prefix: 'gd',
          style: 'camelCase',
        },
      ],
      '@angular-eslint/component-selector': [
        'error',
        {
          type: 'element',
          prefix: 'gd',
          style: 'kebab-case',
        },
      ],
    },
  },
  {
    // ECharts is loaded with the first chart shown, through ECHARTS_IMPORT: its values only in its chunk.
    files: ['src/**/*.ts'],
    ignores: ['src/app/core/charts/echarts-modules.ts'],
    rules: {
      '@typescript-eslint/no-restricted-imports': ['error', { patterns: [echarts] }],
    },
  },
  {
    // Dashboards' views work embedded too, where there is no session: they never reach for it, nor the shell.
    files: ['src/app/features/dashboards/{view,widgets,charts,layout,state,embed}/**/*.ts'],
    ignores: ['**/*.spec.ts'],
    rules: {
      '@typescript-eslint/no-restricted-imports': [
        'error',
        {
          patterns: [
            echarts,
            {
              regex: '(^|/)(core/(auth|changes)|shell)(/|$)',
              message:
                "Dashboards' views are embedded without a session: what they need of it comes from the page's host.",
            },
            {
              regex: '(^|/)features/query/query-results$',
              message:
                'The query results (and AG Grid) load in the Data popup only, signed in: import them in popups/.',
            },
          ],
        },
      ],
    },
  },
  {
    files: ['**/*.html'],
    extends: [angular.configs.templateRecommended, angular.configs.templateAccessibility],
    rules: {},
  },
]);
