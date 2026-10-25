// Live view of a pipeline run: the page is rendered from a snapshot, then server-sent events
// stream everything that happens after it (agent text, tool activity, status changes).
(function () {
    const root = document.getElementById('run');
    if (!root) return;

    const runStatus = document.getElementById('run-status');
    const runError = document.getElementById('run-error');
    const cancelForm = document.getElementById('cancel-form');

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
                break;
        }
    };
})();
