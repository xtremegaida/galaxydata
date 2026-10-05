// The parts of Monaco the application uses, in one chunk loaded when an editor is first shown: the editor and
// its commands, the features for editing scripts, and SQL's languages. Its styles are a stylesheet of their own
// (`monaco.css`, see angular.json), as styles imported by a lazy chunk aren't loaded by the application's builder.
import 'monaco-editor/editor/browser/coreCommands';
import 'monaco-editor/features/bracketMatching/register';
import 'monaco-editor/features/caretOperations/register';
import 'monaco-editor/features/clipboard/register';
import 'monaco-editor/features/codeEditor/register';
import 'monaco-editor/features/codicon/register';
import 'monaco-editor/features/comment/register';
import 'monaco-editor/features/contextmenu/register';
import 'monaco-editor/features/cursorUndo/register';
import 'monaco-editor/features/find/register';
import 'monaco-editor/features/gotoError/register';
import 'monaco-editor/features/hover/register';
import 'monaco-editor/features/indentation/register';
import 'monaco-editor/features/linesOperations/register';
import 'monaco-editor/features/multicursor/register';
import 'monaco-editor/features/readOnlyMessage/register';
import 'monaco-editor/features/smartSelect/register';
import 'monaco-editor/features/toggleTabFocusMode/register';
import 'monaco-editor/features/wordHighlighter/register';
import 'monaco-editor/features/wordOperations/register';
import 'monaco-editor/features/wordPartOperations/register';
import 'monaco-editor/languages/definitions/pgsql/register';
import 'monaco-editor/languages/definitions/sql/register';
export * from 'monaco-editor/editor/editor.api';

(self as unknown as { MonacoEnvironment: unknown }).MonacoEnvironment = {
  getWorker: () => new Worker(new URL('./editor.worker', import.meta.url), { type: 'module' }),
};
