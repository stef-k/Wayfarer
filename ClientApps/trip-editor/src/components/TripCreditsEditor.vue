<script setup lang="ts">
import { message as localize } from '../../../../wwwroot/js/localization/messages.js';
import type { MetadataDraft } from './metadataDraft';

/** Publication applies only to owner contributions; inherited descriptive terms are corrected explicitly. */
defineProps<{ draft: MetadataDraft }>();
</script>

<template>
        <label v-if="draft.isPublic" class="trip-editor-field">
          <span>{{ localize("Editor.OwnerSContributionsPublicationPolicy", "Owner's contributions — publication policy") }}</span>
          <select v-model="draft.contributionPolicy" :aria-label="localize('Editor.PublicationPolicy', 'Publication policy')">
            <option v-if="draft.contributionPolicy === null" :value="null" disabled>{{ localize("Editor.UnlicensedEffectivelyViewOnly", "Unlicensed — effectively view-only") }}</option>
            <option value="CC-BY-4.0">CC BY 4.0</option>
            <option value="CC0-1.0">CC0 1.0</option>
            <option value="view-only">{{ localize("Editor.PublicViewOnly", "Public view-only") }}</option>
          </select>
          <small class="trip-credit-help">{{ localize("Editor.InheritedTermsStaySeparateViewOnlyDoes", "Inherited terms stay separate. View-only does not prevent manual copying or revoke inherited rights.") }}</small>
        </label>

<details class="trip-editor-field">
              <summary>{{ localize("Editor.SourcesCredits", "Sources & credits") }}</summary>
              <p>{{ localize("Editor.PolicyCoversYourContributionsOnlyInheritedTerms", "Policy covers your contributions only. Inherited terms stay separate. Credits are descriptive, not verified ownership. Keep all still-applicable notices.") }}</p>
              <label class="trip-editor-field">{{ localize("Editor.OwnerSContributions", "Owner's contributions") }}
                <select v-model="draft.contributionPolicy">
                  <option v-if="draft.contributionPolicy === null" :value="null" disabled>{{ localize("Editor.UnlicensedEffectivelyViewOnly", "Unlicensed — effectively view-only") }}</option>
                  <option value="CC-BY-4.0">CC BY 4.0</option>
                  <option value="CC0-1.0">CC0 1.0</option>
                  <option value="view-only">{{ localize("Editor.PublicViewOnly", "Public view-only") }}</option>
                </select>
              </label>
              <p v-if="draft.isPublic">{{ localize("Editor.SelectedPublicationPolicy", "Selected publication policy:") }} {{ draft.contributionPolicy }}{{ localize("Editor.InheritedLicensedRightsAreRetained", ". Inherited licensed rights are retained.") }}</p>
              <fieldset v-for="(credit, index) in draft.credits" :key="index">
                <legend>{{ localize("Editor.Credit", "Credit") }} {{ index + 1 }}</legend>
                <label class="trip-editor-field">{{ localize("Editor.Contributor", "Contributor") }} <input v-model="credit.contributor" maxlength="200" /></label>
                <label class="trip-editor-field">{{ localize("Editor.SourceDescription", "Source description") }} <input v-model="credit.sourceTitle" maxlength="200" /></label>
                <label class="trip-editor-field">{{ localize("Editor.InheritedLicenceTerms", "Inherited licence / terms") }} <textarea v-model="credit.licence" maxlength="4000" /></label>
                <label class="trip-editor-field">{{ localize("Editor.LicenceURL", "Licence URL") }} <input v-model="credit.licenceUrl" type="url" maxlength="2000" /></label>
                <label class="trip-editor-field">{{ localize("Editor.Notices", "Notices") }} <textarea v-model="credit.notices" maxlength="4000" /></label>
                <label class="trip-editor-field">{{ localize("Editor.Modifications", "Modifications") }} <textarea v-model="credit.modifications" maxlength="2000" /></label>
                <label class="trip-editor-field">{{ localize("Editor.PublicSourceURL", "Public source URL") }} <input v-model="credit.sourceUrl" type="url" maxlength="2000" /></label>
                <button type="button" @click="draft.credits.splice(index, 1)">{{ localize("Editor.RemoveCorrectedInapplicableCredit", "Remove corrected / inapplicable credit") }}</button>
              </fieldset>
              <button type="button" :disabled="draft.credits.length >= 32" @click="draft.credits.push({ contributor: '', sourceTitle: '', licence: '', licenceUrl: null, notices: null, modifications: null, sourceUrl: null })">{{ localize("Editor.AddCredit", "Add credit") }}</button>
            </details>
</template>

<style scoped>
/* Help text follows the editor theme instead of inheriting field-error colouring. */
.trip-editor-field .trip-credit-help { color: var(--trip-editor-muted); }
</style>
