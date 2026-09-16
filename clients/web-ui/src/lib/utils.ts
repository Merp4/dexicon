/**
 * The project's class-name helper.
 *
 * It re-exports `cn` from shadcn's package of the same name (a compiled drop-in for
 * `twMerge(clsx(...))`). This file exists because `components.json` names `@/lib/utils`
 * as the utils alias, and a component the CLI generates against that alias would import
 * a path that did not exist. One implementation, reachable by the name the config
 * promises.
 */
export { cn } from 'cn';
