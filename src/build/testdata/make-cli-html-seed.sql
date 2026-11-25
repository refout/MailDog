UPDATE bodies
SET text_plain = NULL,
    html_raw = '<p>Hello <b>HTML</b> only. Token: htmlonly.</p><p>Link: <a href="https://example.com">example</a></p>'
WHERE message_row_id = 2;
