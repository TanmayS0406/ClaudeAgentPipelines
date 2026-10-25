// Feature board: renders the cards into their columns, keeps them live over server-sent events, filters them, and
// starts a feature when its card is dragged from New to Planning (or its Start button is clicked).
(function () {
    const board = document.getElementById('board');
    if (!board) return;

    const token = document.querySelector('#board-token input[name="__RequestVerificationToken"]').value;
    const filter = document.getElementById('board-filter');
    const dialog = document.getElementById('new-feature');
    let cards = JSON.parse(document.getElementById('board-data').textContent);

    const statusLabels = { NotStarted: '', Queued: 'Queued', Running: 'In progress', Failed: 'Failed', Cancelled: 'Cancelled', Completed: 'Completed' };

    function el(tag, className, text) {
        const element = document.createElement(tag);
        if (className) element.className = className;
        if (text !== undefined && text !== null) element.textContent = text;
        return element;
    }

    function url(template, id) {
        return template.replace('__id__', encodeURIComponent(id));
    }

    function canStart(card) {
        return card.status !== 'Queued' && card.status !== 'Running';
    }

    function renderCard(card) {
        const item = el('article', 'wi-card');
        item.dataset.id = card.id;
        item.dataset.status = card.status.toLowerCase();
        item.title = card.prompt;
        item.draggable = canStart(card) && card.stage === 'New';

        const head = el('div', 'wi-card-head');
        head.appendChild(el('span', 'wi-icon'));
        head.appendChild(el('span', 'wi-id', card.number));
        const link = el('a', 'wi-card-title', card.title);
        link.href = url(board.dataset.featureUrl, card.id);
        link.draggable = false;
        head.appendChild(link);
        item.appendChild(head);

        const fields = el('div', 'wi-fields');
        const label = statusLabels[card.status];
        if (label) {
            fields.appendChild(el('span', 'wi-tag wi-tag-' + card.status.toLowerCase(), label));
        }
        if (card.stageLabel && card.stageLabel !== card.stage) {
            fields.appendChild(el('span', 'wi-stage muted small', card.stageLabel));
        }
        item.appendChild(fields);

        if (card.tasksTotal > 0) {
            const progress = el('div', 'wi-progress');
            progress.title = card.tasksDone + ' of ' + card.tasksTotal + ' child tasks done';
            const bar = el('div', 'wi-progress-bar');
            const fill = el('span');
            fill.style.width = Math.round(100 * card.tasksDone / card.tasksTotal) + '%';
            bar.appendChild(fill);
            progress.appendChild(bar);
            progress.appendChild(el('span', 'muted small', card.tasksDone + '/' + card.tasksTotal));
            item.appendChild(progress);
        }

        const foot = el('div', 'wi-card-foot');
        const assigned = el('span', 'wi-assigned');
        if (card.assigned.length === 0) {
            assigned.appendChild(el('span', 'muted small', card.status === 'Completed' ? 'Done' : 'Unassigned'));
        }
        card.assigned.forEach(a => {
            const avatar = el('span', 'avatar avatar-' + a.role + ' avatar-busy', a.initials);
            avatar.title = a.name + ' is working on this';
            assigned.appendChild(avatar);
        });
        foot.appendChild(assigned);

        if (card.deploymentUrl) {
            const deployed = el('a', 'small', 'Open app');
            deployed.href = card.deploymentUrl;
            deployed.target = '_blank';
            deployed.rel = 'noopener noreferrer';
            foot.appendChild(deployed);
        }
        if (canStart(card) && card.status !== 'Completed') {
            const start = el('button', 'btn btn-link small', card.status === 'NotStarted' ? '▶ Start' : '↻ Retry');
            start.type = 'button';
            start.dataset.role = 'start';
            foot.appendChild(start);
        }
        item.appendChild(foot);

        if (card.error && card.status === 'Failed') {
            item.appendChild(el('div', 'wi-error small', card.error));
        }
        return item;
    }

    function render() {
        const query = (filter.value || '').trim().toLowerCase();
        board.querySelectorAll('.kanban-column').forEach(column => {
            const list = column.querySelector('[data-role="cards"]');
            const inColumn = cards.filter(c => c.stage === column.dataset.stage);
            const visible = inColumn.filter(c => !query
                || String(c.number) === query.replace('#', '')
                || c.title.toLowerCase().includes(query)
                || c.prompt.toLowerCase().includes(query));
            list.replaceChildren(...visible.map(renderCard));
            column.querySelector('[data-role="count"]').textContent = inColumn.length;
        });
    }

    async function start(id) {
        const response = await fetch(url(board.dataset.startUrl, id), {
            method: 'POST',
            headers: { 'RequestVerificationToken': token, 'Accept': 'application/json' }
        });
        if (!response.ok) {
            const body = await response.json().catch(() => ({}));
            alert(body.error || 'The feature could not be started.');
        }
        // The board refreshes itself over server-sent events.
    }

    // Live updates.
    const source = new EventSource(board.dataset.eventsUrl);
    source.addEventListener('board', message => {
        cards = JSON.parse(message.data).cards;
        render();
    });

    filter.addEventListener('input', render);

    board.addEventListener('click', event => {
        const button = event.target.closest('[data-role="start"]');
        if (button) {
            event.preventDefault();
            button.disabled = true;
            start(button.closest('.wi-card').dataset.id).finally(() => button.disabled = false);
            return;
        }
        if (event.target.closest('[data-role="add"]')) {
            openDialog();
            return;
        }
        // Clicking anywhere on a card opens it, like Azure Boards.
        const card = event.target.closest('.wi-card');
        if (card && !event.target.closest('a, button')) {
            location.href = url(board.dataset.featureUrl, card.dataset.id);
        }
    });

    // Drag a New card onto Planning to start it.
    let dragged = null;
    board.addEventListener('dragstart', event => {
        const card = event.target.closest('.wi-card');
        if (!card) return;
        dragged = card.dataset.id;
        event.dataTransfer.effectAllowed = 'move';
        event.dataTransfer.setData('text/plain', dragged);
        board.classList.add('dragging');
    });
    board.addEventListener('dragend', () => {
        dragged = null;
        board.classList.remove('dragging');
        board.querySelectorAll('.drop-target').forEach(c => c.classList.remove('drop-target'));
    });
    board.addEventListener('dragover', event => {
        const column = event.target.closest('.kanban-column');
        if (!dragged || !column || column.dataset.stage !== 'Planning') return;
        event.preventDefault();
        column.classList.add('drop-target');
    });
    board.addEventListener('dragleave', event => {
        const column = event.target.closest('.kanban-column');
        if (column && !column.contains(event.relatedTarget)) column.classList.remove('drop-target');
    });
    board.addEventListener('drop', event => {
        const column = event.target.closest('.kanban-column');
        if (!dragged || !column || column.dataset.stage !== 'Planning') return;
        event.preventDefault();
        column.classList.remove('drop-target');
        start(dragged);
    });

    // New item dialog.
    function openDialog() {
        dialog.showModal();
        dialog.querySelector('#feature-title').focus();
    }
    document.getElementById('new-item').addEventListener('click', openDialog);
    dialog.querySelector('[data-role="close"]').addEventListener('click', () => dialog.close());

    render();
})();
