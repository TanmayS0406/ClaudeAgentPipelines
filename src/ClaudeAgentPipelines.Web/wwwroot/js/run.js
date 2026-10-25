// Live view of a run (a pipeline, or a feature on a board): the page is rendered from a snapshot, then server-sent
// events stream everything that happens after it (agent text, tool activity, status changes, steps added later).
(function () {
    const root = document.getElementById('run');
    if (!root) return;

    const runStatus = document.getElementById('run-status');
    const runError = document.getElementById('run-error');
    const cancelForm = document.getElementById('cancel-form');
    const runStage = document.getElementById('run-stage');
    const template = document.getElementById('step-template');
    const taskLabels = { Pending: 'To do', Running: 'Doing', Completed: 'Done', Failed: 'Failed', Cancelled: 'Cancelled' };

    function step(agentId) {
        return root.querySelector('[data-agent-id="' + CSS.escape(agentId) + '"]');
    }

    function setStatus(element, status) {
        element.textContent = status;
        element.className = 'status status-' + status.toLowerCase();
    }

    function setStepStatus(card, status) {
        setStatus(card.querySelector('[data-role="status"]'), status);
        card.dataset.status = status.toLowerCase();
        // Child task list of a feature, if the page has one.
        document.querySelectorAll('[data-task-id="' + CSS.escape(card.dataset.agentId) + '"]').forEach(task => {
            task.dataset.state = status.toLowerCase();
            const label = task.querySelector('[data-role="task-state"]');
            if (label) label.textContent = taskLabels[status] || status;
        });
    }

    function addStep(agentId, name, model) {
        if (!template || step(agentId)) return;
        const card = template.content.firstElementChild.cloneNode(true);
        card.dataset.agentId = agentId;
        card.querySelector('.step').textContent = root.querySelectorAll('.step-card').length + 1;
        card.querySelector('[data-role="name"]').textContent = name;
        const badge = card.querySelector('[data-role="model"]');
        badge.textContent = model || '';
        badge.hidden = !model;
        root.appendChild(card);
    }

    root.addEventListener('click', event => {
        const button = event.target.closest('[data-role="copy"]');
        if (!button) return;
        const text = button.closest('.step-card').querySelector('[data-role="output"]').textContent;
        navigator.clipboard.writeText(text).then(() => {
            button.textContent = 'Copied';
            setTimeout(() => button.textContent = 'Copy', 1500);
        });
    });

    if (root.dataset.finished === 'true') return;

    const source = new EventSource(root.dataset.eventsUrl);

    source.addEventListener('end', () => source.close());

    source.onmessage = message => {
        const evt = JSON.parse(message.data);
        const card = evt.agentId ? step(evt.agentId) : null;

        switch (evt.type) {
            case 'stepAdded':
                addStep(evt.agentId, evt.text, evt.data && evt.data.model);
                break;
            case 'stage':
                if (runStage) runStage.textContent = evt.text;
                break;
            case 'stepStarted':
                if (card) setStepStatus(card, 'Running');
                break;
            case 'text':
                if (card) {
                    const output = card.querySelector('[data-role="output"]');
                    const atBottom = window.innerHeight + window.scrollY >= document.body.scrollHeight - 40;
                    output.textContent += evt.text;
                    if (atBottom) window.scrollTo(0, document.body.scrollHeight);
                }
                break;
            case 'activity':
                if (card) {
                    const item = document.createElement('li');
                    item.textContent = '› ' + evt.text;
                    card.querySelector('[data-role="activity"]').appendChild(item);
                }
                break;
            case 'usage':
                if (card) {
                    card.querySelector('.tokens').textContent =
                        evt.data.inputTokens.toLocaleString() + ' in / ' + evt.data.outputTokens.toLocaleString() + ' out tokens';
                }
                break;
            case 'stepCompleted':
                if (card) setStepStatus(card, 'Completed');
                break;
            case 'stepFailed':
                if (card) {
                    setStepStatus(card, 'Failed');
                    const error = card.querySelector('[data-role="error"]');
                    error.textContent = evt.text;
                    error.hidden = false;
                }
                break;
            case 'runFinished':
                setStatus(runStatus, evt.data.status);
                cancelForm.hidden = true;
                root.querySelectorAll('[data-status="pending"], [data-status="running"]').forEach(c =>
                    setStepStatus(c, evt.data.status === 'Completed' ? 'Completed'
                        : c.dataset.status === 'running' && evt.data.status === 'Failed' ? 'Failed' : 'Cancelled'));
                if (evt.text && evt.data.status !== 'Cancelled') {
                    runError.textContent = evt.text;
                    runError.hidden = false;
                }
                // Feature pages reload to show the feature's final state (stage, deployment URL).
                if (root.dataset.reloadOnFinish === 'true') setTimeout(() => location.reload(), 1500);
                break;
        }
    };
})();
