import { message, number } from '../localization/messages.js';
import { encodeFeatureText as encode } from './feature-metadata.js';

/** Both history tables keep the same sliding window and numeric page input. */
export const renderHistoryPagination = (container, totalPages, currentPage, goToPage) => {
    container.replaceChildren();
    if (totalPages <= 1) return;

    const start = Math.max(1, currentPage - 3);
    const end = Math.min(totalPages, currentPage + 3);
    const page = (label, index, disabled = false, active = false) => `
        <li class="page-item ${active ? 'active' : ''}">
            <button type="button" class="page-link" data-page="${index}"
                    ${disabled ? 'disabled' : ''} ${active ? 'aria-current="page"' : ''}>${encode(label)}</button>
        </li>`;
    const ellipsis = '<li class="page-item disabled"><span class="page-link">…</span></li>';
    const pages = [];
    for (let index = start; index <= end; index++) {
        pages.push(page(number(index), index, false, index === currentPage));
    }

    const label = message('Shell.PageNumber', 'Page');
    container.innerHTML = `
        <nav aria-label="${encode(message('Shell.Pagination', 'Pagination'))}" class="mt-2">
            <div class="d-flex flex-wrap justify-content-center align-items-center gap-2">
                <ul class="pagination mb-0">
                    ${page(message('Shell.FirstPage', 'First'), 1, currentPage === 1)}
                    ${page(message('Shell.PriorPage', 'Previous'), currentPage - 1, currentPage === 1)}
                    ${start > 1 ? ellipsis : ''}
                    ${pages.join('')}
                    ${end < totalPages ? ellipsis : ''}
                    ${page(message('Shell.NextPage', 'Next'), currentPage + 1, currentPage === totalPages)}
                    ${page(message('Shell.LastPage', 'Last'), totalPages, currentPage === totalPages)}
                </ul>
                <form class="input-group input-group-sm" style="width:120px;">
                    <input type="number" id="jumpPageInput" class="form-control" min="1" max="${totalPages}"
                           placeholder="${encode(label)}" aria-label="${encode(label)}" required>
                    <button class="btn btn-outline-secondary" type="submit">${encode(message('Shell.GoToPage', 'Go'))}</button>
                </form>
            </div>
        </nav>`;
    const nav = container.firstElementChild;
    nav.addEventListener('click', event => {
        const target = event.target.closest('[data-page]');
        if (target) goToPage(Number(target.dataset.page));
    });
    const jump = nav.querySelector('form');
    jump.addEventListener('submit', event => {
        event.preventDefault();
        goToPage(jump.querySelector('input').valueAsNumber);
    });
};
