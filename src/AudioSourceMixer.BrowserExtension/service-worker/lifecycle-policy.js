export function shouldConnectNativeOnRecovery(graphs) {
  return Array.isArray(graphs) && graphs.some((graph) => Number.isInteger(graph?.tabId));
}
