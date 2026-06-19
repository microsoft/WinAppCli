import { closeSharedEditor } from './shared-context';

export default async function globalTeardown() {
    await closeSharedEditor();
}
