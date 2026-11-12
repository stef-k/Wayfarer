<script setup lang="ts">
import type { MetadataDraft } from './metadataDraft';

/** Publication applies only to owner contributions; inherited descriptive terms are corrected explicitly. */
defineProps<{ draft: MetadataDraft }>();
</script>

<template>
        <label v-if="draft.isPublic" class="trip-editor-field">
          <span>Owner's contributions — publication policy</span>
          <select v-model="draft.contributionPolicy" aria-label="Publication policy">
            <option v-if="draft.contributionPolicy === null" :value="null" disabled>Unlicensed — effectively view-only</option>
            <option value="CC-BY-4.0">CC BY 4.0</option>
            <option value="CC0-1.0">CC0 1.0</option>
            <option value="view-only">Public view-only</option>
          </select>
          <small>Inherited terms stay separate. View-only does not prevent manual copying or revoke inherited rights.</small>
        </label>

<details class="trip-editor-field">
              <summary>Sources &amp; credits</summary>
              <p>Policy covers your contributions only. Inherited terms stay separate. Credits are descriptive, not verified ownership. Keep all still-applicable notices.</p>
              <label class="trip-editor-field">Owner's contributions
                <select v-model="draft.contributionPolicy">
                  <option v-if="draft.contributionPolicy === null" :value="null" disabled>Unlicensed — effectively view-only</option>
                  <option value="CC-BY-4.0">CC BY 4.0</option>
                  <option value="CC0-1.0">CC0 1.0</option>
                  <option value="view-only">Public view-only</option>
                </select>
              </label>
              <p v-if="draft.isPublic">Selected publication policy: {{ draft.contributionPolicy }}. Inherited licensed rights are retained.</p>
              <fieldset v-for="(credit, index) in draft.credits" :key="index">
                <legend>Credit {{ index + 1 }}</legend>
                <label class="trip-editor-field">Contributor <input v-model="credit.contributor" maxlength="200" /></label>
                <label class="trip-editor-field">Source description <input v-model="credit.sourceTitle" maxlength="200" /></label>
                <label class="trip-editor-field">Inherited licence / terms <textarea v-model="credit.licence" maxlength="4000" /></label>
                <label class="trip-editor-field">Licence URL <input v-model="credit.licenceUrl" type="url" maxlength="2000" /></label>
                <label class="trip-editor-field">Notices <textarea v-model="credit.notices" maxlength="4000" /></label>
                <label class="trip-editor-field">Modifications <textarea v-model="credit.modifications" maxlength="2000" /></label>
                <label class="trip-editor-field">Public source URL <input v-model="credit.sourceUrl" type="url" maxlength="2000" /></label>
                <button type="button" @click="draft.credits.splice(index, 1)">Remove corrected / inapplicable credit</button>
              </fieldset>
              <button type="button" :disabled="draft.credits.length >= 32" @click="draft.credits.push({ contributor: '', sourceTitle: '', licence: '', licenceUrl: null, notices: null, modifications: null, sourceUrl: null })">Add credit</button>
            </details>
</template>
