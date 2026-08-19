export type ReviewState = { pageType: string; includedInWorkflow: boolean }
export type ReviewCommand = { before: ReviewState; after: ReviewState }
export type ReviewCommandHistory = { undo: ReviewCommand[]; redo: ReviewCommand[] }
