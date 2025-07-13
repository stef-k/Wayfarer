// storeInstance.js
// use this to create the store 
// also use it to import the store in other modules
// in order to avoid circular imports
import { createStore } from './store.js';

export const store = createStore({
    name: 'wayfarer',
    enableDevPanel: true
});
