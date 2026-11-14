import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import Page from './Page';

// The plugin's page alone, for working on it with vite's dev server.
createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <Page />
  </StrictMode>,
);
