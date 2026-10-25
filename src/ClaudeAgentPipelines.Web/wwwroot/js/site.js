// Forms with data-confirm ask before submitting.
document.addEventListener('submit', event => {
    const message = event.target.dataset && event.target.dataset.confirm;
    if (message && !window.confirm(message)) event.preventDefault();
});
